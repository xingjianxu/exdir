'use strict';

// exdir 在线更新（GitHub Release）回归用的**本机 mock feed**（纯 node，无第三方依赖）。
//
//   node server.js --zip <zip 路径> [--tag v99.0.20991231] [--notes <md 文件>]
//                  [--asset-name <名字>] [--port 0] [--no-digest] [--bad-digest]
//                  [--release-url <url>]
//
// 两个路由（形状照着 GitHub 的公开接口来，见 Services/UpdateJsonContext.cs 里的 DTO）：
//   GET /latest  → “最新 Release”的 JSON（tag_name / body / html_url / assets[]）
//   GET /asset/<资产名> → 那个 zip（--zip 指的文件）
//
// 就绪时在 stdout 打一行 `READY {json}`（里面有实际端口），之后一直监听，由测试脚本 Kill。
//
// 为什么要它：exdir 的“发现新版本 → 下载 → 校验 SHA256 → 退出替换 → 重启”整条路，
// 靠本机 mock 就能验完 —— 不用发真实的 Release，也不会把开发机上的 exdir 换成别的东西
// （回归脚本把 dist 复制到临时目录里当“安装目录”，见 tools/test-update.ps1）。
//
// 加 `EXDIR_UPDATE_FEED=http://127.0.0.1:<port>/latest` 就能让 exdir 认这个 feed
// （见 Services/UpdateService.cs 里的 FeedVariable）。

const http = require('http');
const fs = require('fs');
const crypto = require('crypto');
const path = require('path');

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
if (!args.zip || args.zip === true) {
  console.error('用法: node server.js --zip <zip 路径> [--tag v99.0.20991231] [--notes <md>] [--port 0]');
  process.exit(2);
}

const zipPath = path.resolve(args.zip);
if (!fs.existsSync(zipPath)) {
  console.error(`找不到 zip: ${zipPath}`);
  process.exit(2);
}

const tag = args.tag || 'v99.0.20991231';
const assetName = args['asset-name'] || `exdir-${tag}-win-x64.zip`;
const notes = args.notes && args.notes !== true
  ? fs.readFileSync(path.resolve(args.notes), 'utf8')
  : `### 测试用发行说明（mock feed）\n\n- tag: \`${tag}\`\n- 这份说明由 tools/update-test-server/server.js 提供\n`;
const port = Number(args.port || 0);

const zipBuffer = fs.readFileSync(zipPath);
const realDigest = `sha256:${crypto.createHash('sha256').update(zipBuffer).digest('hex')}`;
// --bad-digest：故意给一个错的摘要，用来验“校验失败就不装”这条反面路径
const digest = args['no-digest'] ? null
  : args['bad-digest'] ? 'sha256:' + '0'.repeat(64)
    : realDigest;

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  console.log(`[mock] ${req.method} ${url.pathname}`);

  if (url.pathname === '/latest') {
    const base = `http://127.0.0.1:${server.address().port}`;
    const asset = {
      name: assetName,
      size: zipBuffer.length,
      browser_download_url: `${base}/asset/${encodeURIComponent(assetName)}`,
    };
    if (digest) asset.digest = digest;

    const body = {
      tag_name: tag,
      name: `exdir ${tag}`,
      body: notes,
      html_url: args['release-url'] && args['release-url'] !== true
        ? args['release-url']
        : `${base}/release`,
      published_at: '2099-12-31T00:00:00Z',
      prerelease: false,
      draft: false,
      assets: [asset],
    };

    const payload = Buffer.from(JSON.stringify(body), 'utf8');
    res.writeHead(200, { 'content-type': 'application/json; charset=utf-8', 'content-length': payload.length });
    res.end(payload);
    return;
  }

  if (url.pathname.startsWith('/asset/')) {
    const name = decodeURIComponent(url.pathname.slice('/asset/'.length));
    if (name !== assetName) {
      res.writeHead(404).end('not found');
      return;
    }

    res.writeHead(200, { 'content-type': 'application/zip', 'content-length': zipBuffer.length });
    res.end(zipBuffer);
    return;
  }

  res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
  res.end('mock feed 只有 /latest 与 /asset/<名字>\n');
});

server.listen(port, '127.0.0.1', () => {
  const info = {
    port: server.address().port,
    tag,
    assetName,
    zip: zipPath,
    size: zipBuffer.length,
    digest,
    realDigest,
  };
  console.log('READY ' + JSON.stringify(info));
});
