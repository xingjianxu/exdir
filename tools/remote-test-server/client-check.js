// 用 ssh2 自带的客户端连一下测试服务器，验证服务器这一侧是否协议正确。
const { Client } = require('ssh2');

const port = Number(process.argv[2] || 2222);
const withKey = process.argv[3] === '--key';

const config = {
  host: '127.0.0.1',
  port,
  username: 'testuser',
  tryKeyboard: false,
};

if (withKey) {
  config.privateKey = require('fs').readFileSync(process.argv[4]);
} else {
  config.password = 'testpass';
}

const conn = new Client();

conn.on('ready', () => {
  conn.sftp((err, sftp) => {
    if (err) {
      console.log('SFTP-ERROR', err.message);
      process.exit(1);
    }

    sftp.readdir('/', (listErr, list) => {
      if (listErr) {
        console.log('READDIR-ERROR', listErr.message);
        process.exit(1);
      }

      console.log('ENTRIES', list.map((e) => `${e.filename}${e.attrs.isDirectory() ? '/' : ''}(${e.attrs.size})`).join(','));

      sftp.stat('/hello.txt', (statErr, stat) => {
        console.log('STAT', statErr ? 'ERROR ' + statErr.message : `size=${stat.size} dir=${stat.isDirectory()}`);

        sftp.createReadStream('/sub/inner.txt')
          .on('data', (chunk) => console.log('DATA', JSON.stringify(chunk.toString())))
          .on('end', () => {
            conn.end();
            process.exit(0);
          })
          .on('error', (streamErr) => {
            console.log('STREAM-ERROR', streamErr.message);
            process.exit(1);
          });
      });
    });
  });
});

conn.on('error', (err) => {
  console.log('CONN-ERROR', err.message);
  process.exit(1);
});

conn.connect(config);
