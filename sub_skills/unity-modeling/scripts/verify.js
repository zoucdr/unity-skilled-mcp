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
const PNG = require('./png');
const lathe = require('./lathe');

function configure(opts) { return mcp.configure(opts); }

/**
 * 程序化曲面（旋转体 / 扫掠管）的在线校验。
 * 与图集流水线的假设不同：这里是多 submesh + 柱面 UV + 无图集，
 * 所以不查 uv_info 覆盖率，改查 submesh/材质数、贴图宽高比、外壁法线朝外比例。
 */
async function runLathe(o) {
  const opt = o || {};
  const checks = [];
  const problems = [];
  const add = (name, ok, detail) => {
    checks.push({ name, ok, detail });
    if (!ok) problems.push(`${name}：${detail}`);
  };

  const name = opt.object || 'MugBodyMesh';
  const code = [
    'GameObject go = GameObject.Find(' + JSON.stringify(name) + ');',
    'if (go == null) { Debug.Log("LATHE: missing"); }',
    'else {',
    '    MeshFilter mf = go.GetComponent<MeshFilter>();',
    '    MeshRenderer mr = go.GetComponent<MeshRenderer>();',
    '    if (mf == null || mf.sharedMesh == null) { Debug.Log("LATHE: n_mesh"); }',
    '    else {',
    '        Mesh m = mf.sharedMesh;',
    '        int mats = (mr != null && mr.sharedMaterials != null) ? mr.sharedMaterials.Length : -1;',
    '        Vector3[] v = m.vertices;',
    '        int[] t = m.GetTriangles(0);',
    '        int outward = 0; int total = 0;',
    '        for (int i = 0; i < t.Length; i += 3) {',
    '            Vector3 a = v[t[i]]; Vector3 b = v[t[i+1]]; Vector3 c = v[t[i+2]];',
    '            Vector3 n = Vector3.Cross(b - a, c - a);',
    '            Vector3 radial = new Vector3(a.x, 0f, a.z);',
    '            if (radial.sqrMagnitude < 0.0001f) continue;',
    '            total++;',
    '            if (Vector3.Dot(n, radial) > 0f) outward++;',
    '        }',
    '        Debug.Log("LATHE: subs=" + m.subMeshCount + " mats=" + mats',
    '            + " tris=" + (t.Length / 3) + " outward=" + outward + "/" + total);',
    '    }',
    '}',
  ].join('\n');
  try {
    const r = await mcp.call('code.execute', { mode: 'runtime', code, description: 'lathe verify' });
    const log = String((r && r.logs) || '');
    const m = /LATHE: subs=(\d+) mats=(-?\d+) tris=(\d+) outward=(\d+)\/(\d+)/.exec(log);
    if (!m) {
      add('曲面网格可读', false, log.trim().slice(0, 160) || 'C# 没回报 LATHE 数据');
    } else {
      const subs = Number(m[1]), mats = Number(m[2]);
      const tris = Number(m[3]), outward = Number(m[4]), total = Number(m[5]);
      const want = opt.expectSubMeshes === undefined ? 3 : opt.expectSubMeshes;
      add('submesh 数量', subs === want, `subMeshCount=${subs}（期望 ${want}）`);
      add('材质数匹配 submesh', mats === subs, `sharedMaterials=${mats} / subMeshCount=${subs}`);
      add('网格非空', tris > 0, `外壁三角形 ${tris}`);
      const ratio = total ? outward / total : 0;
      const min = opt.outwardMinRatio === undefined ? lathe.LIMITS.outwardMinRatio : opt.outwardMinRatio;
      add('外壁法线朝外', ratio >= min,
        `${outward}/${total} = ${(ratio * 100).toFixed(1)}%（下限 ${(min * 100).toFixed(0)}%）`);
    }
  } catch (e) {
    add('曲面网格可读', false, e.message.split('\n')[0]);
  }

  // 贴图宽高比：直接读实际 PNG，不依赖调用方手填尺寸
  if (opt.texture && Number.isFinite(opt.radius) && Number.isFinite(opt.height)) {
    let abs;
    try { abs = mcp.resolveOut(opt.texture, opt.projectRoot); }
    catch (e) { abs = opt.texture; }
    if (!fs.existsSync(abs)) {
      add('贴图存在', false, `找不到 ${abs}`);
    } else {
      add('贴图存在', true, path.basename(abs));
      try {
        const img = PNG.readPNG(abs);
        const a = lathe.assertTextureAspect([img.w, img.h], { radius: opt.radius, height: opt.height });
        add('柱面贴图宽高比', true, `${img.w}×${img.h} = ${a.got.toFixed(2)} vs ${a.want.toFixed(2)}`);
      } catch (e) {
        add('柱面贴图宽高比', false, e.message.split('\n')[0]);
      }
    }
  }

  const ok = problems.length === 0;
  if (!ok && !opt.noThrow) {
    const err = new Error('曲面校验未通过：\n  - ' + problems.join('\n  - '));
    err.checks = checks;
    err.problems = problems;
    throw err;
  }
  return { ok, checks, problems };
}

/**
 * 跑全部检查。
 * @returns {object} { ok, checks:[{name, ok, detail}], problems:[] }
 */
async function run(o) {
  const opt = o || {};
  if (opt.profile === 'lathe') return await runLathe(opt);
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

module.exports = { configure, run, runLathe, report };
