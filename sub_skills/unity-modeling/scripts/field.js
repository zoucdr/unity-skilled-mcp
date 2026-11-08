'use strict';
/**
 * field.js — 安全的高度场 / 标量场。
 *
 * 存在的理由：`Float32Array[5.5]` 在 JS 里**不会报错**，只会静默丢弃写入。
 * 曾经因此让整张高度场退化成平面、法线贴图全平，却排查了很久才定位到
 * 一句 `GAP_PX * 0.5`。这里把「静默失败」全部转成「立刻抛错」。
 *
 * 规则：
 *   1) get/set/add 的索引必须是整数，非整数直接抛 RangeError，并提示用 Math.round；
 *   2) 越界直接抛，不静默截断（需要平铺请显式用 setWrap）；
 *   3) 写入 NaN / Infinity 直接抛；
 *   4) 提供 assertNotFlat()，把「贴图出来是一片平」这种结果问题前置成断言问题。
 */

class Field {
  constructor(w, h, fill = 0) {
    if (!Number.isInteger(w) || !Number.isInteger(h) || w <= 0 || h <= 0) {
      throw new RangeError(`Field 尺寸必须是正整数，收到 ${w}x${h}`);
    }
    this.w = w;
    this.h = h;
    this.data = new Float32Array(w * h);
    if (fill !== 0) this.data.fill(fill);
  }

  /** 索引校验：这里是全部收益所在。 */
  idx(x, y, op) {
    if (!Number.isInteger(x) || !Number.isInteger(y)) {
      throw new RangeError(
        `Field.${op}: 索引必须是整数，收到 x=${x} y=${y}。` +
        `非整数索引会让 TypedArray 静默丢弃写入（高度场会整体变平），请用 Math.round()。`
      );
    }
    if (x < 0 || y < 0 || x >= this.w || y >= this.h) {
      throw new RangeError(`Field.${op}: 越界 (${x},${y})，画布 ${this.w}x${this.h}`);
    }
    return y * this.w + x;
  }

  get(x, y) { return this.data[this.idx(x, y, 'get')]; }

  set(x, y, v) {
    if (!Number.isFinite(v)) throw new TypeError(`Field.set: 值必须是有限数，收到 ${v} @(${x},${y})`);
    this.data[this.idx(x, y, 'set')] = v;
    return this;
  }

  add(x, y, d) {
    if (!Number.isFinite(d)) throw new TypeError(`Field.add: 增量必须是有限数，收到 ${d} @(${x},${y})`);
    const i = this.idx(x, y, 'add');
    this.data[i] += d;
    return this;
  }

  /** 水平方向无缝平铺写入（纹理 wrap 用），x 取模、y 仍严格校验。 */
  setWrap(x, y, v) {
    if (!Number.isInteger(x) || !Number.isInteger(y)) {
      throw new RangeError(`Field.setWrap: 索引必须是整数，收到 x=${x} y=${y}`);
    }
    if (!Number.isFinite(v)) throw new TypeError(`Field.setWrap: 值必须是有限数，收到 ${v}`);
    const xx = ((x % this.w) + this.w) % this.w;
    this.data[this.idx(xx, y, 'setWrap')] = v;
    return this;
  }

  getWrap(x, y) {
    if (!Number.isInteger(x) || !Number.isInteger(y)) {
      throw new RangeError(`Field.getWrap: 索引必须是整数，收到 x=${x} y=${y}`);
    }
    const xx = ((x % this.w) + this.w) % this.w;
    const yy = ((y % this.h) + this.h) % this.h;
    return this.data[yy * this.w + xx];
  }

  /** 双线性采样，允许非整数坐标（这是唯一接受小数的读路径）。 */
  sample(x, y) {
    if (!Number.isFinite(x) || !Number.isFinite(y)) {
      throw new TypeError(`Field.sample: 坐标必须是有限数，收到 (${x},${y})`);
    }
    const x0 = Math.floor(x), y0 = Math.floor(y);
    const fx = x - x0, fy = y - y0;
    const g = (a, b) => this.getWrap(a, b);
    const v00 = g(x0, y0), v10 = g(x0 + 1, y0);
    const v01 = g(x0, y0 + 1), v11 = g(x0 + 1, y0 + 1);
    return (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
  }

  fill(v) {
    if (!Number.isFinite(v)) throw new TypeError(`Field.fill: ${v}`);
    this.data.fill(v);
    return this;
  }

  map(fn) {
    for (let i = 0; i < this.data.length; i++) {
      const v = fn(this.data[i], i % this.w, (i / this.w) | 0);
      if (!Number.isFinite(v)) throw new TypeError(`Field.map 回调返回非有限数 ${v}`);
      this.data[i] = v;
    }
    return this;
  }

  clone() {
    const f = new Field(this.w, this.h);
    f.data.set(this.data);
    return f;
  }

  /** 统计量：min/max/mean/dev。dev 是判断「有没有做出起伏」的关键指标。 */
  stats() {
    const n = this.data.length;
    let min = Infinity, max = -Infinity, sum = 0;
    for (let i = 0; i < n; i++) {
      const v = this.data[i];
      if (v < min) min = v;
      if (v > max) max = v;
      sum += v;
    }
    const mean = sum / n;
    let acc = 0;
    for (let i = 0; i < n; i++) { const d = this.data[i] - mean; acc += d * d; }
    return { min, max, mean, dev: Math.sqrt(acc / n), n };
  }

  /**
   * 平台期检查：dev 太小说明场根本没被写进去（常见原因就是上面的浮点索引）。
   * 把它放在生成法线之前调用，能把「渲染出来才发现的平」提前到生成时暴露。
   */
  assertNotFlat(name, eps = 1e-4) {
    const s = this.stats();
    if (!(s.dev > eps)) {
      throw new Error(
        `${name} 高度场几乎没有起伏（dev=${s.dev.toExponential(3)} <= ${eps}）。` +
        `多半是写入索引不是整数被静默丢弃，或所有写入落在同一位置。`
      );
    }
    return s;
  }

  /** 环绕盒式模糊，用于接触阴影 / 柔化。半径必须是正整数。 */
  boxBlur(radius) {
    if (!Number.isInteger(radius) || radius < 1) {
      throw new RangeError(`boxBlur: radius 必须是 >=1 的整数，收到 ${radius}`);
    }
    const out = new Field(this.w, this.h);
    const norm = 1 / ((2 * radius + 1) * (2 * radius + 1));
    for (let y = 0; y < this.h; y++) {
      for (let x = 0; x < this.w; x++) {
        let s = 0;
        for (let dy = -radius; dy <= radius; dy++) {
          for (let dx = -radius; dx <= radius; dx++) s += this.getWrap(x + dx, y + dy);
        }
        out.set(x, y, s * norm);
      }
    }
    return out;
  }
}

/** 供「模糊高度 - 自身高度」这类接触阴影用法直接相减。 */
function subtract(a, b) {
  if (a.w !== b.w || a.h !== b.h) throw new Error('subtract: 尺寸不一致');
  const out = new Field(a.w, a.h);
  for (let i = 0; i < a.data.length; i++) out.data[i] = a.data[i] - b.data[i];
  return out;
}

/** 中心差分求切线空间法线，返回 nx/ny/nz 三个 Field（未编码）。 */
function gradientToNormal(H, ppm, strength = 1) {
  if (!Number.isFinite(ppm) || ppm <= 0) throw new RangeError(`ppm 必须为正数，收到 ${ppm}`);
  const nx = new Field(H.w, H.h), ny = new Field(H.w, H.h), nz = new Field(H.w, H.h);
  for (let y = 0; y < H.h; y++) {
    for (let x = 0; x < H.w; x++) {
      const dhx = (H.getWrap(x + 1, y) - H.getWrap(x - 1, y)) * 0.5 * ppm * strength;
      const dhy = (H.getWrap(x, y + 1) - H.getWrap(x, y - 1)) * 0.5 * ppm * strength;
      // 图像 y 向下、切线空间 y 向上：nx 取负、ny 取正
      let vx = -dhx, vy = dhy, vz = 1;
      const len = Math.hypot(vx, vy, vz);
      vx /= len; vy /= len; vz /= len;
      nx.set(x, y, vx); ny.set(x, y, vy); nz.set(x, y, vz);
    }
  }
  return { nx, ny, nz };
}

module.exports = { Field, subtract, gradientToNormal };
