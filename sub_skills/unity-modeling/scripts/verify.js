'use strict';
/**
 * verify.js — 建模结果综合校验：一次调用回答"这活儿交得出去吗"。
 *
 * 检查项（每项都可单独跑）：
 *   1) 单 mesh：subMeshCount 是否符合预期（默认 1）
 *   2) 网格非空：三角形数 > 0
 *   3) UV 覆盖：uv_info 的覆盖率，太低说明图集没做或 UV 丢了
 *   4) 贴图质量：借用 inspect.js 的纯色/过曝/无缝/法线检查
 *   5) 材质挂载：材质数 == submesh 数，贴图槽位非空
 *
 * 设计原则：**失败就抛，不返回"大概没问题"**。
 * 每一条失败信息都指出现象 + 可能原因 + 该跑哪个命令复查。
 *
 * 用法：
 *   const V = require('./verify');
 *   V.configure({ project: '...' });
 *   await V.run({ object: 'KidsTable_Mesh', meshPath: 'Assets/.../Merged.asset',
 *                 texture: 'Assets/.../Atlas.png', expectSubMeshes: 1 });
 */

const path = require('path');
const fs = require('fs');
const mcp = require('./mcp');
const model = require('./model');
const tex = require('./tex');
const inspect = require('./inspect');

function configure(opts) { return mcp.configure(opts); }

/**
 * 跑全部检查。
 * @returns {object} { ok, checks:[{name, ok, detail}], problems:[] }
 */
async function run(o) {
  const opt = o || {};
  const project = (mcp.configure && opt.project) || undefined;
  const checks = [];
  const problems = [];

  const add = (name, ok, detail) => {
    checks.push({ name, ok, detail });
    if (!ok) problems.push(`${name}：${detail}`);
  };

  // 1 + 2：单 mesh
  if (opt.object) {
    try {
      const info = await model.info(opt.object);
      const want = opt.expectSubMeshes === undefined ? 1 : opt.expectSubMeshes;
      const sm = info.subMeshCount !== undefined ? info.subMeshCount : info.submeshes;
      add('单 mesh', Number(sm) === want, `subMeshCount=${sm}（期望 ${want}）`);

      const tris = info.triangleCount !== undefined
        ? info.triangleCount
        : (Array.isArray(info.trianglesPerSubMesh) ? info.trianglesPerSubMesh.reduce((a, b) => a + b, 0) : undefined);
      add('网格非空', tris > 0, `三角形 ${tris}`);

      // 顶点数异常多通常是"忘了合并"的迹象
      if (info.vertexCount) {
        const perTri = tris ? info.vertexCount / tris : 0;
        add('顶点冗余', perTri < 8, `顶点/三角形 = ${perTri.toFixed(2)}（>8 可能有大量重复顶点，考虑 mesh.optimize）`);
      }
    } catch (e) {
      add('单 mesh', false, e.message.split('\n')[0]);
    }
  }

  // 3：UV 覆盖
  const meshPath = opt.meshPath;
  if (meshPath) {
    try {
      const uvi = await tex.uvInfo(meshPath, { width: opt.width || 512, height: opt.height || 512 });
      const cov = uvi.coverage_ratio !== undefined ? uvi.coverage_ratio : uvi.coverage;
      const covNum = typeof cov === 'string' ? parseFloat(cov) / 100 : cov;
      add('UV 覆盖率', covNum === undefined || covNum > 0.3,
        `覆盖率 ${cov}（<0.3 说明图集没做或 UV 塌陷）`);
      const overlap = uvi.overlap_pixels !== undefined ? uvi.overlap_pixels : uvi.overlap;
      if (overlap !== undefined) add('UV 无跨岛重叠', Number(overlap) === 0, `重叠像素 ${overlap}`);
    } catch (e) {
      add('UV 覆盖率', false, e.message.split('\n')[0]);
    }
  }

  // 4：贴图质量
  if (opt.texture) {
    // 相对路径统一交给 mcp.resolveOut：显式 projectRoot > 配置 > 运行时推导
    let texAbs;
    try {
      texAbs = mcp.resolveOut(opt.texture, opt.projectRoot);
    } catch (e) {
      texAbs = opt.texture;
    }
    if (!fs.existsSync(texAbs)) {
      add('贴图存在', false, `找不到文件 ${texAbs}`);
    } else {
      add('贴图存在', true, path.basename(texAbs));
      try {
        const rep = inspect.analyze(texAbs, { normal: !!opt.isNormal });
        add('贴图非纯色', !rep.flat, rep.flat ? '整张几乎是纯色，检查 layers 是否生效' : '有内容');
        const maxed = rep.channelsStats.filter(c => c.channel !== 'a' && c.max >= 255).length;
        add('贴图未过曝', maxed < 2, maxed >= 2 ? `${maxed} 个通道顶到 255` : '正常');
        if (rep.warnings && rep.warnings.length) {
          rep.warnings.forEach(w => add('贴图警告', false, w));
        }
      } catch (e) {
        add('贴图质量', false, e.message.split('\n')[0]);
      }
    }
  }

  // 5：材质挂载
  if (opt.material) {
    try {
      const mi = await mcp.call('material.get_info', { path: opt.material });
      const props = (mi && (mi.properties || mi.textures)) || {};
      const hasMain = Object.keys(props).some(k => /_MainTex|_BaseMap|_BaseColorMap/i.test(k));
      add('材质有主贴图', hasMain, hasMain ? '已挂' : '主贴图槽位为空');
      if (opt.isNormal) {
        const hasBump = Object.keys(props).some(k => /_BumpMap|_NormalMap/i.test(k));
        add('材质有法线图', hasBump, hasBump ? '已挂' : '法线槽位为空');
      }
    } catch (e) {
      add('材质信息', false, e.message.split('\n')[0]);
    }
  }

  // 6：对象存在
  if (opt.object) {
    try {
      const found = await model.findObjects('^' + String(opt.object).replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$');
      add('对象存在', found.length > 0, found.length ? found[0].path : '场景里找不到');
    } catch (e) { /* 已在上面的 info 检查里体现 */ }
  }

  const ok = problems.length === 0;
  if (!ok && !opt.noThrow) {
    const err = new Error('校验未通过：\n  - ' + problems.join('\n  - '));
    err.checks = checks;
    err.problems = problems;
    throw err;
  }
  return { ok, checks, problems };
}

/** 打印成表格。 */
function report(result) {
  const lines = ['校验结果：' + (result.ok ? '全部通过' : `${result.problems.length} 项失败`), ''];
  const w = Math.max(...result.checks.map(c => c.name.length), 4);
  for (const c of result.checks) {
    lines.push(`  ${c.ok ? 'OK  ' : 'FAIL'}  ${c.name.padEnd(w)}  ${c.detail}`);
  }
  return lines.join('\n');
}

module.exports = { configure, run, report };
