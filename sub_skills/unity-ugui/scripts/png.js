'use strict';
/**
 * png.js — 零依赖 PNG 编解码，重点是**解码要真的能解**。
 *
 * 曾经的坑：只处理 filter 0（None）的极简解码器读 Unity 导出的 PNG
 * （Unity `EncodeToPNG` 会大量使用 Sub / Up / Average / Paeth），结果解出来
 * 全是噪点，据此做的 A/B 对比图完全无效，白费一轮排查。
 *
 * 本实现支持：bitDepth 8、非隔行、colorType 0/2/4/6，filter 0..4 全支持。
 * 不支持的格式**抛错而不是返回垃圾数据** —— 这一点比支持更多格式更重要。
 */
const zlib = require('zlib');

const CRC_TABLE = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c;
  }
  return t;
})();

function crc32(buf) {
  let c = -1;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(td), 0);
  return Buffer.concat([len, td, crc]);
}

const SIG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

function paeth(a, b, c) {
  const p = a + b - c;
  const pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
  if (pa <= pb && pa <= pc) return a;
  if (pb <= pc) return b;
  return c;
}

/**
 * 解码 PNG。
 * @returns {{w:number,h:number,channels:number,colorType:number,data:Buffer}}
 *          data 为逐像素紧凑排列（channels 1/3/4），行优先、无 padding。
 */
function decodePNG(buf) {
  if (buf.length < 8 || !buf.subarray(0, 8).equals(SIG)) {
    throw new Error('不是 PNG（签名不符）');
  }
  let p = 8;
  let ihdr = null;
  const idat = [];
  let palette = null;
  while (p < buf.length - 8) {
    const len = buf.readUInt32BE(p);
    const type = buf.toString('ascii', p + 4, p + 8);
    const data = buf.subarray(p + 8, p + 8 + len);
    if (type === 'IHDR') {
      ihdr = {
        w: data.readUInt32BE(0),
        h: data.readUInt32BE(4),
        depth: data[8],
        colorType: data[9],
        compression: data[10],
        filter: data[11],
        interlace: data[12],
      };
    } else if (type === 'PLTE') {
      palette = data;
    } else if (type === 'IDAT') {
      idat.push(data);
    } else if (type === 'IEND') {
      break;
    }
    p += 12 + len;
  }
  if (!ihdr) throw new Error('PNG 缺少 IHDR');
  if (ihdr.depth !== 8) throw new Error(`只支持 8bit，收到 bitDepth=${ihdr.depth}`);
  if (ihdr.interlace !== 0) throw new Error(`不支持隔行 PNG（interlace=${ihdr.interlace}）`);
  const CH = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 };
  const channels = CH[ihdr.colorType];
  if (channels === undefined) throw new Error(`不支持的 colorType=${ihdr.colorType}`);

  const raw = zlib.inflateSync(Buffer.concat(idat));
  const bpp = channels;                     // 8bit：每像素字节数
  const stride = ihdr.w * bpp;
  const need = (stride + 1) * ihdr.h;
  if (raw.length < need) {
    throw new Error(`IDAT 数据不足：需要 ${need} 字节，实得 ${raw.length}`);
  }

  const out = Buffer.alloc(stride * ihdr.h);
  for (let y = 0; y < ihdr.h; y++) {
    const ft = raw[y * (stride + 1)];
    const src = y * (stride + 1) + 1;
    const dst = y * stride;
    const prev = (y - 1) * stride;
    switch (ft) {
      case 0:
        raw.copy(out, dst, src, src + stride);
        break;
      case 1:
        for (let i = 0; i < stride; i++) {
          const a = i >= bpp ? out[dst + i - bpp] : 0;
          out[dst + i] = (raw[src + i] + a) & 0xff;
        }
        break;
      case 2:
        for (let i = 0; i < stride; i++) {
          const b = y > 0 ? out[prev + i] : 0;
          out[dst + i] = (raw[src + i] + b) & 0xff;
        }
        break;
      case 3:
        for (let i = 0; i < stride; i++) {
          const a = i >= bpp ? out[dst + i - bpp] : 0;
          const b = y > 0 ? out[prev + i] : 0;
          out[dst + i] = (raw[src + i] + ((a + b) >> 1)) & 0xff;
        }
        break;
      case 4:
        for (let i = 0; i < stride; i++) {
          const a = i >= bpp ? out[dst + i - bpp] : 0;
          const b = y > 0 ? out[prev + i] : 0;
          const c = i >= bpp && y > 0 ? out[prev + i - bpp] : 0;
          out[dst + i] = (raw[src + i] + paeth(a, b, c)) & 0xff;
        }
        break;
      default:
        throw new Error(`第 ${y} 行 filter 类型非法：${ft}`);
    }
  }

  // 调色板图展开成 RGB，方便统一处理
  if (ihdr.colorType === 3) {
    if (!palette) throw new Error('调色板 PNG 缺少 PLTE');
    const rgb = Buffer.alloc(ihdr.w * ihdr.h * 3);
    for (let i = 0; i < ihdr.w * ihdr.h; i++) {
      const k = out[i] * 3;
      rgb[i * 3] = palette[k];
      rgb[i * 3 + 1] = palette[k + 1] || 0;
      rgb[i * 3 + 2] = palette[k + 2] || 0;
    }
    return { w: ihdr.w, h: ihdr.h, channels: 3, colorType: ihdr.colorType, data: rgb };
  }

  return { w: ihdr.w, h: ihdr.h, channels, colorType: ihdr.colorType, data: out };
}

/** 灰度图统一取第 0 通道。 */
function grayOf(img) {
  const { w, h, channels, data } = img;
  const g = new Uint8Array(w * h);
  for (let i = 0; i < w * h; i++) g[i] = data[i * channels];
  return g;
}

/** 统计图像「相邻像素差」的均值，用来区分真图与解码失败产生的噪点。 */
function neighborDelta(img) {
  const { w, h, channels, data } = img;
  let acc = 0, n = 0;
  for (let y = 0; y < h; y++) {
    for (let x = 1; x < w; x++) {
      for (let c = 0; c < Math.min(channels, 3); c++) {
        const i = (y * w + x) * channels + c;
        acc += Math.abs(data[i] - data[i - channels]);
        n++;
      }
    }
  }
  return acc / n;
}

/**
 * 编码 PNG。
 * @param {number} w @param {number} h
 * @param {Buffer} pix 长度 w*h*3（RGB）或 w*h*4（RGBA）
 * @param {object} [opt] { alpha?:boolean, level?:number }
 */
function encodePNG(w, h, pix, opt = {}) {
  const alpha = opt.alpha || pix.length === w * h * 4;
  const ch = alpha ? 4 : 3;
  if (pix.length !== w * h * ch) {
    throw new Error(`像素数据长度不符：期望 ${w * h * ch}，实得 ${pix.length}`);
  }
  const rowBytes = w * ch;
  const raw = Buffer.alloc((rowBytes + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (rowBytes + 1)] = 0; // filter None：输出侧简单且完全合法
    pix.copy(raw, y * (rowBytes + 1) + 1, y * rowBytes, (y + 1) * rowBytes);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8;
  ihdr[9] = alpha ? 6 : 2;
  return Buffer.concat([
    SIG,
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: opt.level === undefined ? 9 : opt.level })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

function readPNG(file) {
  return decodePNG(require('fs').readFileSync(file));
}

function writePNG(file, w, h, pix, opt) {
  require('fs').writeFileSync(file, encodePNG(w, h, pix, opt));
}

module.exports = { decodePNG, encodePNG, readPNG, writePNG, grayOf, neighborDelta, paeth };
