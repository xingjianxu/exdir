'use strict';

// exdir 的远程位置（SFTP / FTP）回归用的**本机测试服务器**。
//
//   node server.js --root <目录> [--ftp-port 2121] [--sftp-port 2222]
//                  [--user testuser] [--password testpass] [--key-out <私钥落盘路径>]
//
// 两端都只做**只读**那一半（列目录 + 取文件）：回归脚本要验的是“点击浏览 / 下载”，
// 上传删除这些 exdir 目前压根没实现。SFTP 用的是 ssh2 的 SFTP 服务器接口，
// FTP 用 ftp-srv；都在 127.0.0.1 上监听、端口写死由脚本传进来（不能用 0：脚本要提前知道端口）。
//
// 就绪时往 stdout 打一行 READY + JSON（端口 / 客户端私钥路径），脚本靠它拿信息；
// 之后就一直挂着，由脚本最后 Kill。

const { constants, writeFileSync, statSync, readdirSync, openSync, readSync, closeSync, realpathSync } = require('fs');
const { timingSafeEqual } = require('crypto');
const path = require('path');
const { Server, utils } = require('ssh2');
const { OPEN_MODE, STATUS_CODE } = require('ssh2/lib/protocol/SFTP.js');
const { FtpSrv } = require('ftp-srv');

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 1) {
    const key = argv[i];
    if (!key.startsWith('--')) continue;
    args[key.slice(2)] = argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[++i] : true;
  }
  return args;
}

const args = parseArgs(process.argv.slice(2));
const root = realpathSync(args.root || process.cwd());
const ftpPort = Number(args['ftp-port'] || 2121);
const sftpPort = Number(args['sftp-port'] || 2222);
const user = args.user || 'testuser';
const password = args.password || 'testpass';
const keyOut = args['key-out'] || path.join(root, '..', 'exdir-test-key');
const verbose = Boolean(args.verbose);
const trace = (message) => {
  if (verbose) console.error('[server] ' + message);
};

// 客户端私钥（给 SFTP 的“私钥登录”用例）：RSA 2048，OpenSSH 格式的私钥 + ssh-rsa 公钥
const clientKey = utils.generateKeyPairSync('rsa', { bits: 2048 });
writeFileSync(keyOut, clientKey.private, 'utf8');
const allowedKey = utils.parseKey(clientKey.public);

// ------------------------------------------------------------------ 公共：把“服务器上的路径”映射到本机目录

function resolveLocal(remotePath) {
  const normalized = path.posix.normalize('/' + String(remotePath || '/').replace(/\\/g, '/'));
  const local = path.join(root, normalized);
  const resolved = path.resolve(local);

  if (resolved !== root && !resolved.startsWith(root + path.sep)) {
    return null; // 越界（../）：直接当不存在
  }

  return resolved;
}

function statOf(localPath) {
  try {
    const stat = statSync(localPath);
    return {
      isDirectory: stat.isDirectory(),
      size: stat.size,
      mtime: stat.mtimeMs / 1000,
      atime: stat.atimeMs / 1000,
    };
  } catch {
    return null;
  }
}

function sftpAttrs(remotePath, localPath) {
  const stat = statOf(localPath);
  if (!stat) return null;

  let mode = stat.isDirectory ? constants.S_IFDIR : constants.S_IFREG;
  mode |= stat.isDirectory ? 0o755 : 0o644;

  return {
    mode,
    uid: 0,
    gid: 0,
    size: stat.isDirectory ? 0 : stat.size,
    atime: Math.floor(stat.atime),
    mtime: Math.floor(stat.mtime),
  };
}

function longName(name, attrs) {
  const type = (attrs.mode & constants.S_IFDIR) ? 'd' : '-';
  const size = String(attrs.size).padStart(8);
  return `${type}rwxr-xr-x 1 testuser testuser ${size} Jan  1 00:00 ${name}`;
}

// ------------------------------------------------------------------ SFTP

const server = new Server({ hostKeys: [clientKey.private], debug: verbose ? (m) => trace(String(m)) : undefined }, (client) => {
  client.on('authentication', (ctx) => {
    const sameUser = ctx.username === user;
    const sameBytes = (input, expected) => {
      const a = Buffer.from(input);
      const b = Buffer.from(expected);
      return a.length === b.length && timingSafeEqual(a, b);
    };

    switch (ctx.method) {
      case 'password':
        if (sameUser && sameBytes(ctx.password, password)) return ctx.accept();
        return ctx.reject();

      case 'publickey':
        if (sameUser && ctx.key.algo === allowedKey.type
            && Buffer.compare(ctx.key.data, allowedKey.getPublicSSH()) === 0) {
          if (ctx.signature) {
            return allowedKey.verify(ctx.blob, ctx.signature, ctx.hashAlgo)
              ? ctx.accept()
              : ctx.reject();
          }
          return ctx.accept(); // 只探测公钥能否用，还没带签名
        }
        return ctx.reject();

      case 'none':
        // 让客户端知道有哪些认证方式可用
        return sameUser ? ctx.reject(['password', 'publickey']) : ctx.reject();

      default:
        return ctx.reject();
    }
  }).on('ready', () => {
    client.on('session', (accept) => {
      const session = accept();
      session.on('sftp', (acceptSftp) => {
        const sftp = acceptSftp();
        const directories = new Map();
        const files = new Map();
        let nextHandle = 1;

        const handleBuffer = (id) => {
          const handle = Buffer.alloc(4);
          handle.writeUInt32BE(id, 0, true);
          return handle;
        };

        const handleId = (handle) => handle.readUInt32BE(0, true);

        sftp.on('REALPATH', (reqid, remotePath) => {
          const normalized = path.posix.normalize('/' + String(remotePath || '/').replace(/\\/g, '/'));
          sftp.name(reqid, [{ filename: normalized, longname: normalized, attrs: {} }]);
        })
          .on('OPENDIR', (reqid, remotePath) => {
            trace(`OPENDIR ${remotePath}`);
            const local = resolveLocal(remotePath);
            const stat = local ? statOf(local) : null;

            if (!stat || !stat.isDirectory) return sftp.status(reqid, STATUS_CODE.NO_SUCH_FILE);

            const id = nextHandle++;
            directories.set(id, { local, sent: false });
            return sftp.handle(reqid, handleBuffer(id));
          })
          .on('READDIR', (reqid, handle) => {
            const state = directories.get(handleId(handle));
            trace(`READDIR handle=${handleId(handle)} found=${Boolean(state)} sent=${state && state.sent}`);
            if (!state) return sftp.status(reqid, STATUS_CODE.FAILURE);

            // 一次给完，然后**不**跟着发 STATUS：
            // SSH.NET 的 READDIR 是“一个请求一个响应”——NAME 那一拍就把请求结了，
            // 同一个 reqid 再来一个 STATUS 会被当成“无效响应”（见 lib/protocol/SFTP.js 的注释）。
            // 客户端的下一次 READDIR（同一个 handle）才拿到 EOF。
            if (state.sent) return sftp.status(reqid, STATUS_CODE.EOF);
            state.sent = true;

            const names = [];

            for (const entry of readdirSync(state.local, { withFileTypes: true })) {
              const attrs = sftpAttrs(entry.name, path.join(state.local, entry.name)) || {};
              names.push({ filename: entry.name, longname: longName(entry.name, attrs), attrs });
            }

            sftp.name(reqid, names);
            trace(`READDIR ${state.local} → ${names.length} 项`);
          })
          .on('STAT', onStat)
          .on('LSTAT', onStat)
          .on('FSTAT', (reqid, handle) => {
            const state = files.get(handleId(handle)) || directories.get(handleId(handle));
            if (!state) return sftp.status(reqid, STATUS_CODE.FAILURE);

            const attrs = sftpAttrs('', state.local);
            return attrs ? sftp.attrs(reqid, attrs) : sftp.status(reqid, STATUS_CODE.NO_SUCH_FILE);
          })
          .on('OPEN', (reqid, remotePath, flags) => {
            if (!(flags & OPEN_MODE.READ)) return sftp.status(reqid, STATUS_CODE.PERMISSION_DENIED);

            const local = resolveLocal(remotePath);
            const stat = local ? statOf(local) : null;

            if (!stat || stat.isDirectory) return sftp.status(reqid, STATUS_CODE.NO_SUCH_FILE);

            let fd;
            try {
              fd = openSync(local, 'r');
            } catch {
              return sftp.status(reqid, STATUS_CODE.FAILURE);
            }

            const id = nextHandle++;
            files.set(id, { local, fd, position: 0 });
            return sftp.handle(reqid, handleBuffer(id));
          })
          .on('READ', (reqid, handle, offset, length) => {
            const state = files.get(handleId(handle));
            if (!state) return sftp.status(reqid, STATUS_CODE.FAILURE);

            const buffer = Buffer.alloc(length);
            let read = 0;

            try {
              read = readSync(state.fd, buffer, 0, length, offset);
            } catch {
              return sftp.status(reqid, STATUS_CODE.FAILURE);
            }

            state.position = offset + read;

            if (read === 0) return sftp.status(reqid, STATUS_CODE.EOF);
            return sftp.data(reqid, buffer.subarray(0, read));
          })
          .on('CLOSE', (reqid, handle) => {
            const id = handleId(handle);

            if (directories.delete(id)) return sftp.status(reqid, STATUS_CODE.OK);

            const state = files.get(id);
            if (state) {
              try {
                closeSync(state.fd);
              } catch {
                // 关不掉也无所谓
              }
              files.delete(id);
              return sftp.status(reqid, STATUS_CODE.OK);
            }

            return sftp.status(reqid, STATUS_CODE.FAILURE);
          });

        function onStat(reqid, remotePath) {
          const local = resolveLocal(remotePath);
          const attrs = local ? sftpAttrs(remotePath, local) : null;

          return attrs ? sftp.attrs(reqid, attrs) : sftp.status(reqid, STATUS_CODE.NO_SUCH_FILE);
        }
      });
    });
  });
});

// ------------------------------------------------------------------ FTP

const ftp = new FtpSrv({
  url: `ftp://127.0.0.1:${ftpPort}`,
  pasv_url: '127.0.0.1',
  pasv_min: 31100,
  pasv_max: 31200,
  anonymous: false,
  greeting: 'exdir test ftp',
  // ftp-srv 的 log 选项要的是 bunyan logger 实例（不是配置对象）；调成只报致命错误，
  // 免得列目录的每条日志都涌进 stderr
  log: require('bunyan').createLogger({ name: 'exdir-ftp-test', level: 'fatal' }),
});

ftp.on('login', ({ username, password: given }, resolve, reject) => {
  if (username === user && given === password) {
    resolve({ root });
  } else {
    reject(new Error('bad credentials'));
  }
});

ftp.listen().then(() => {
  server.listen(sftpPort, '127.0.0.1', () => {
    console.log(`READY ${JSON.stringify({ ftpPort, sftpPort, user, password, root, privateKey: keyOut })}`);
  });
}).catch((error) => {
  console.error('启动测试服务器失败：', error && error.message ? error.message : error);
  process.exit(1);
});

process.on('SIGTERM', () => process.exit(0));
process.on('SIGINT', () => process.exit(0));
