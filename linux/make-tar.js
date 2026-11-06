// Writes a .tar.gz with Unix permissions (Windows' tar can't mark files executable).
//   node make-tar.js <out.tar.gz> <folder-in-archive> <file>[:mode] ...
// Each file goes in <folder-in-archive>/<its name>; mode is octal, default 644.
const fs = require('fs'), path = require('path'), zlib = require('zlib');
const [out, folder, ...files] = process.argv.slice(2);
if (!out || !folder || files.length === 0) { console.error('usage: node make-tar.js out.tar.gz folder file[:mode] ...'); process.exit(2); }

function header(name, size, mode, type) {
  const h = Buffer.alloc(512);
  const put = (s, off, len) => h.write(s, off, len, 'utf8');
  const oct = (n, len) => n.toString(8).padStart(len - 1, '0') + '\0';
  if (Buffer.byteLength(name) > 100) throw new Error('name too long: ' + name);
  put(name, 0, 100);
  put(oct(mode, 8), 100, 8);
  put(oct(0, 8), 108, 8); put(oct(0, 8), 116, 8);            // uid, gid
  put(oct(size, 12), 124, 12);
  put(oct(Math.floor(Date.now() / 1000), 12), 136, 12);
  put('        ', 148, 8);                                    // checksum placeholder
  put(type, 156, 1);
  put('ustar\0', 257, 6); put('00', 263, 2);
  put('root', 265, 32); put('root', 297, 32);
  let sum = 0; for (const b of h) sum += b;
  put(sum.toString(8).padStart(6, '0') + '\0 ', 148, 8);
  return h;
}

const parts = [header(folder + '/', 0, 0o755, '5')];
for (const spec of files) {
  const m = spec.match(/^(.*?)(?::([0-7]{3}))?$/);
  const file = m[1], mode = parseInt(m[2] || '644', 8);
  const data = fs.readFileSync(file);
  parts.push(header(`${folder}/${path.basename(file)}`, data.length, mode, '0'), data, Buffer.alloc((512 - (data.length % 512)) % 512));
}
parts.push(Buffer.alloc(1024)); // end of archive
fs.writeFileSync(out, zlib.gzipSync(Buffer.concat(parts), { level: 9 }));
console.log(`${out} (${(fs.statSync(out).size / 1048576).toFixed(1)} MB)`);
