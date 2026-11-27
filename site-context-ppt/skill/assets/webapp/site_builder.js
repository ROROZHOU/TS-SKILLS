/* =====================================================================
   站点数据运行时构建器
   ---------------------------------------------------------------------
   SiteBuilder.build({lng, lat}, onStep) 返回周边图层 L、城市图层 C 和道路升级任务。
   ===================================================================== */
(function () {
  'use strict';

  var TILE_HOSTS = ['webst01', 'webst02', 'webst03', 'webst04'];

  /* ---------------------------------------------------------------- 坐标 */
  function lngToPx(lng, z) { return (lng + 180) / 360 * 256 * Math.pow(2, z); }
  function latToPx(lat, z) {
    var s = Math.sin(lat * Math.PI / 180);
    return (0.5 - Math.log((1 + s) / (1 - s)) / (4 * Math.PI)) * 256 * Math.pow(2, z);
  }
  function pxToLng(x, z) { return x / (256 * Math.pow(2, z)) * 360 - 180; }
  function pxToLat(y, z) {
    var n = Math.PI - 2 * Math.PI * y / (256 * Math.pow(2, z));
    return 180 / Math.PI * Math.atan(0.5 * (Math.exp(n) - Math.exp(-n)));
  }

  /* ---------------------------------------------------------------- 抓瓦片 */
  function fetchTile(style, z, x, y, retry) {
    var host = TILE_HOSTS[(x + y + (retry || 0)) & 3];
    var url = 'https://' + host + '.is.autonavi.com/appmaptile?style=' + style +
              '&x=' + x + '&y=' + y + '&z=' + z + (retry ? '&r=' + Math.random() : '');
    // fetch 没有超时，个别请求会挂住不返回，整批就永远 pending —— 必须自己掐
    var ctrl = new AbortController();
    var timer = setTimeout(function () { try { ctrl.abort(); } catch (e) {} }, 9000);
    return fetch(url, { mode: 'cors', cache: 'default', signal: ctrl.signal })
      .then(function (r) { return r.ok ? r.blob() : null; })
      .then(function (b) {
        clearTimeout(timer);
        if (!b) return null;
        // 偶尔会拿到 100~200 字节的空壳图（代理/限流），重试一次换主机和随机参数
        if (b.size < 400 && !retry) return fetchTile(style, z, x, y, 1);
        return createImageBitmap(b);
      })
      .catch(function () {
        clearTimeout(timer);
        return retry ? null : fetchTile(style, z, x, y, 1);
      });
  }

  // 并发上限，别把瓦片服务打崩
  function pool(tasks, limit) {
    var i = 0, out = new Array(tasks.length);
    function next() {
      if (i >= tasks.length) return Promise.resolve();
      var k = i++;
      return tasks[k]().then(function (v) { out[k] = v; return next(); });
    }
    var runners = [];
    for (var j = 0; j < Math.min(limit, tasks.length); j++) runners.push(next());
    return Promise.all(runners).then(function () { return out; });
  }

  /* 抓一块以 (lng,lat) 为中心、halfM 米见方一半的图像，返回 ImageData + mpp */
  function grab(style, z, lng, lat, halfM, maxPx) {
    var mpp = 156543.03392 * Math.cos(lat * Math.PI / 180) / Math.pow(2, z);
    var halfPx = Math.round(halfM / mpp);
    // 太大就降级别（或缩放），免得几千万像素卡死
    if (maxPx && halfPx * 2 > maxPx) {
      var f = (maxPx / 2) / halfPx;
      z = z - Math.ceil(Math.log(1 / f) / Math.LN2);
      mpp = 156543.03392 * Math.cos(lat * Math.PI / 180) / Math.pow(2, z);
      halfPx = Math.round(halfM / mpp);
    }
    var size = halfPx * 2;
    var cx = lngToPx(lng, z), cy = latToPx(lat, z);
    var x0 = Math.round(cx - halfPx), y0 = Math.round(cy - halfPx);
    var tx0 = Math.floor(x0 / 256), ty0 = Math.floor(y0 / 256);
    var tx1 = Math.floor((x0 + size - 1) / 256), ty1 = Math.floor((y0 + size - 1) / 256);
    var cols = tx1 - tx0 + 1, rows = ty1 - ty0 + 1;

    var cv = document.createElement('canvas');
    cv.width = cols * 256; cv.height = rows * 256;
    var ctx = cv.getContext('2d', { willReadFrequently: true });
    ctx.fillStyle = '#000';
    ctx.fillRect(0, 0, cv.width, cv.height);

    var jobs = [];
    for (var ty = ty0; ty <= ty1; ty++) {
      for (var tx = tx0; tx <= tx1; tx++) {
        (function (tx, ty, ox, oy) {
          jobs.push(function () {
            return fetchTile(style, z, tx, ty).then(function (bm) {
              if (!bm) return false;
              ctx.drawImage(bm, ox, oy);
              return true;
            });
          });
        })(tx, ty, (tx - tx0) * 256, (ty - ty0) * 256);
      }
    }
    return pool(jobs, 12).then(function (got) {
      var ok = 0;
      for (var g = 0; g < got.length; g++) if (got[g] === true) ok++;
      var ox = x0 - tx0 * 256, oy = y0 - ty0 * 256;
      var img = ctx.getImageData(ox, oy, size, size);
      if (window.__sbLog) window.__sbLog.push('grab style=' + style + ' z=' + z +
        ' tiles ' + ok + '/' + jobs.length + ' size=' + size);
      return { data: img.data, w: size, h: size, mpp: mpp, z: z, x0: x0, y0: y0 };
    });
  }

  /* ---------------------------------------------------------------- 掩膜 */
  function maskFrom(g, fn) {
    var d = g.data, n = g.w * g.h, m = new Uint8Array(n);
    for (var i = 0, j = 0; i < n; i++, j += 4) {
      m[i] = fn(d[j], d[j + 1], d[j + 2], d[j + 3]) ? 1 : 0;
    }
    return m;
  }

  /* 开运算（先腐蚀后膨胀），去掉 1~2 px 的噪点与字线 */
  function erode(m, w, h, r) {
    var out = new Uint8Array(m.length);
    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var ok = 1, i = y * w + x;
        if (!m[i]) continue;
        for (var dy = -r; dy <= r && ok; dy++) {
          var yy = y + dy; if (yy < 0 || yy >= h) { ok = 0; break; }
          for (var dx = -r; dx <= r; dx++) {
            if (dx * dx + dy * dy > r * r + r) continue;
            var xx = x + dx; if (xx < 0 || xx >= w) { ok = 0; break; }
            if (!m[yy * w + xx]) { ok = 0; break; }
          }
        }
        out[i] = ok;
      }
    }
    return out;
  }
  function dilate(m, w, h, r) {
    var out = new Uint8Array(m.length);
    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        if (!m[y * w + x]) continue;
        for (var dy = -r; dy <= r; dy++) {
          var yy = y + dy; if (yy < 0 || yy >= h) continue;
          for (var dx = -r; dx <= r; dx++) {
            if (dx * dx + dy * dy > r * r + r) continue;
            var xx = x + dx; if (xx < 0 || xx >= w) continue;
            out[yy * w + xx] = 1;
          }
        }
      }
    }
    return out;
  }
  function open(m, w, h, r) { return dilate(erode(m, w, h, r), w, h, r); }

  /* 去掉面积过小的连通块 */
  function dropSmall(m, w, h, minPx) {
    var seen = new Uint8Array(m.length), keep = new Uint8Array(m.length);
    var stack = new Int32Array(m.length);
    for (var s = 0; s < m.length; s++) {
      if (!m[s] || seen[s]) continue;
      var sp = 0; stack[sp++] = s; seen[s] = 1;
      var cells = [];
      while (sp) {
        var p = stack[--sp]; cells.push(p);
        var px = p % w, py = (p - px) / w;
        for (var k = 0; k < 8; k++) {
          var nx = px + (k === 0 ? -1 : k === 1 ? 0 : k === 2 ? 1 : k === 3 ? -1 :
                         k === 4 ? 1 : k === 5 ? -1 : k === 6 ? 0 : 1);
          var ny = py + (k === 0 ? -1 : k === 1 ? -1 : k === 2 ? -1 : k === 3 ? 0 :
                         k === 4 ? 0 : k === 5 ? 1 : k === 6 ? 1 : 1);
          if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
          var q = ny * w + nx;
          if (m[q] && !seen[q]) { seen[q] = 1; stack[sp++] = q; }
        }
      }
      if (cells.length >= minPx) for (var c = 0; c < cells.length; c++) keep[cells[c]] = 1;
    }
    return keep;
  }

  /* ---------------------------------------------------------------- 轮廓 */
  /* Moore 邻域边界追踪：从最上最左的边界点出发，绕连通块走一圈 */
  var N8 = [[-1, 0], [-1, 1], [0, 1], [1, 1], [1, 0], [1, -1], [0, -1], [-1, -1]];

  function traceContours(m, w, h, minPx) {
    var seen = new Uint8Array(m.length), out = [];
    var stack = new Int32Array(m.length);
    for (var s = 0; s < m.length; s++) {
      if (!m[s] || seen[s]) continue;
      // 先标记连通域，统计大小
      var sp = 0; stack[sp++] = s; seen[s] = 1;
      var cells = 0, start = s, sx = s % w, sy = (s - sx) / w;
      while (sp) {
        var p = stack[--sp]; cells++;
        var px = p % w, py = (p - px) / w;
        if (py < sy || (py === sy && px < sx)) { start = p; sx = px; sy = py; }
        for (var k = 0; k < 8; k++) {
          var nx = px + N8[k][0], ny = py + N8[k][1];
          if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
          var q = ny * w + nx;
          if (m[q] && !seen[q]) { seen[q] = 1; stack[sp++] = q; }
        }
      }
      if (cells < minPx) continue;

      // 该连通域的边界追踪（用未标记的独立数组判断，避免跨块串味）
      var poly = walk(m, w, h, sx, sy);
      if (poly && poly.length >= 4) out.push(poly);
    }
    return out;
  }

  function walk(m, w, h, sx, sy) {
    var dir = 7;         // 从左边开始找
    var cx = sx, cy = sy;
    var pts = [], guard = 0, maxStep = Math.min(w * h * 2, 200000);
    var startDir = -1;
    while (guard++ < maxStep) {
      pts.push([cx, cy]);
      var found = false;
      for (var k = 0; k < 8; k++) {
        var d = (dir + 6 + k) % 8;       // 从上一方向的左后方开始扫
        var nx = cx + N8[d][0], ny = cy + N8[d][1];
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
        if (!m[ny * w + nx]) continue;
        // 回到起点且方向重复 → 闭合
        if (nx === sx && ny === sy) {
          if (startDir === d) return pts;
          if (startDir < 0) startDir = d;
        }
        dir = d; cx = nx; cy = ny; found = true;
        break;
      }
      if (!found) break;
    }
    return pts.length >= 4 ? pts : null;
  }

  /* Douglas-Peucker（迭代版） */
  function simplify(pts, tol) {
    if (pts.length < 3) return pts;
    var keep = new Uint8Array(pts.length); keep[0] = keep[pts.length - 1] = 1;
    var stack = [[0, pts.length - 1]];
    while (stack.length) {
      var seg = stack.pop(), a = seg[0], b = seg[1];
      if (b - a < 2) continue;
      var ax = pts[a][0], ay = pts[a][1], bx = pts[b][0], by = pts[b][1];
      var dx = bx - ax, dy = by - ay, L2 = dx * dx + dy * dy;
      var best = -1, bi = a;
      for (var i = a + 1; i < b; i++) {
        var px = pts[i][0], py = pts[i][1], d;
        if (L2 < 1e-9) d = Math.hypot(px - ax, py - ay);
        else {
          var t = ((px - ax) * dx + (py - ay) * dy) / L2;
          t = t < 0 ? 0 : t > 1 ? 1 : t;
          d = Math.hypot(px - (ax + t * dx), py - (ay + t * dy));
        }
        if (d > best) { best = d; bi = i; }
      }
      if (best > tol) { keep[bi] = 1; stack.push([a, bi], [bi, b]); }
    }
    var out = [];
    for (var j = 0; j < pts.length; j++) if (keep[j]) out.push(pts[j]);
    return out;
  }

  function ringArea(pts) {
    var s = 0;
    for (var i = 0; i < pts.length; i++) {
      var a = pts[i], b = pts[(i + 1) % pts.length];
      s += a[0] * b[1] - b[0] * a[1];
    }
    return s / 2;
  }

  /* ---------------------------------------------------------------- 骨架 */
  /* 距离变换（chamfer 3-4，两遍扫描）：每个前景像素到最近背景的距离。
     取骨架点上的这个值就是"半宽"，用来把胡同和主次干道分开 ——
     style=8 里只有主干道是黄的，次干和支路都是浅灰，颜色和建筑底图几乎一样，
     靠 RGB 阈值根本分不开（阈值松了收进几千条假路，紧了只剩几条大马路）。
     宽度是唯一能稳定区分两者的量。 */
  function distTransform(m, w, h) {
    var d = new Float32Array(w * h), INF = 1e6, i, x, y, v, u;
    for (i = 0; i < m.length; i++) d[i] = m[i] ? INF : 0;
    for (y = 0; y < h; y++) {
      for (x = 0; x < w; x++) {
        i = y * w + x; v = d[i];
        if (!v) continue;
        if (x > 0 && d[i - 1] + 1 < v) v = d[i - 1] + 1;
        if (y > 0) {
          if (d[i - w] + 1 < v) v = d[i - w] + 1;
          if (x > 0 && d[i - w - 1] + 1.414 < v) v = d[i - w - 1] + 1.414;
          if (x < w - 1 && d[i - w + 1] + 1.414 < v) v = d[i - w + 1] + 1.414;
        }
        d[i] = v;
      }
    }
    for (y = h - 1; y >= 0; y--) {
      for (x = w - 1; x >= 0; x--) {
        i = y * w + x; u = d[i];
        if (!u) continue;
        if (x < w - 1 && d[i + 1] + 1 < u) u = d[i + 1] + 1;
        if (y < h - 1) {
          if (d[i + w] + 1 < u) u = d[i + w] + 1;
          if (x < w - 1 && d[i + w + 1] + 1.414 < u) u = d[i + w + 1] + 1.414;
          if (x > 0 && d[i + w - 1] + 1.414 < u) u = d[i + w - 1] + 1.414;
        }
        d[i] = u;
      }
    }
    return d;
  }

  function skeletonize(m, w, h) {
    var img = m, changed = true, guard = 0;
    var idx = new Int32Array(w * h);
    while (changed && guard++ < 40) {
      changed = false;
      for (var pass = 0; pass < 2; pass++) {
        var del = [];
        for (var y = 1; y < h - 1; y++) {
          for (var x = 1; x < w - 1; x++) {
            var i = y * w + x;
            if (!img[i]) continue;
            var p2 = img[i - w], p3 = img[i - w + 1], p4 = img[i + 1],
                p5 = img[i + w + 1], p6 = img[i + w], p7 = img[i + w - 1],
                p8 = img[i - 1], p9 = img[i - w - 1];
            var B = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
            if (B < 2 || B > 6) continue;
            var seq = [p2, p3, p4, p5, p6, p7, p8, p9, p2];
            var A = 0;
            for (var k = 0; k < 8; k++) if (seq[k] === 0 && seq[k + 1] === 1) A++;
            if (A !== 1) continue;
            if (pass === 0) {
              if (p2 * p4 * p6 !== 0) continue;
              if (p4 * p6 * p8 !== 0) continue;
            } else {
              if (p2 * p4 * p8 !== 0) continue;
              if (p2 * p6 * p8 !== 0) continue;
            }
            del.push(i);
          }
        }
        if (del.length) {
          changed = true;
          for (var q = 0; q < del.length; q++) img[del[q]] = 0;
        }
      }
    }
    return img;
  }

  /* 骨架 → 折线：先按 8 邻域度数找节点，再从节点沿边走出链条。
     dt 传入距离变换时，顺带算出每条链的半宽 hw（像素），用于按路宽分级。 */
  function traceSkeleton(sk, w, h, mpp, dt) {
    var pts = [], ids = new Int32Array(w * h).fill(-1);
    for (var i = 0; i < sk.length; i++) if (sk[i]) { ids[i] = pts.length; pts.push(i); }
    if (!pts.length) return [];

    var deg = new Int32Array(pts.length);
    var nb = [];
    for (var a = 0; a < pts.length; a++) {
      var p = pts[a], px = p % w, py = (p - px) / w;
      var list = [];
      for (var k = 0; k < 8; k++) {
        var nx = px + N8[k][0], ny = py + N8[k][1];
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
        var q = ids[ny * w + nx];
        if (q >= 0) list.push(q);
      }
      nb.push(list); deg[a] = list.length;
    }

    var usedEdge = {};
    function ek(a2, b2) { return a2 < b2 ? a2 + '_' + b2 : b2 + '_' + a2; }
    var chains = [];

    function extend(from, prev) {
      var path = [prev, from];
      var cur = from, last = prev;
      for (var step = 0; step < 100000; step++) {
        if (deg[cur] !== 2) break;
        var nxt = -1;
        for (var t = 0; t < nb[cur].length; t++) {
          if (nb[cur][t] !== last && !usedEdge[ek(cur, nb[cur][t])]) { nxt = nb[cur][t]; break; }
        }
        if (nxt < 0) break;
        usedEdge[ek(cur, nxt)] = 1;
        path.push(nxt); last = cur; cur = nxt;
      }
      return path;
    }

    var order = [];
    for (var s = 0; s < pts.length; s++) if (deg[s] !== 2) order.push(s);
    for (var s2 = 0; s2 < pts.length; s2++) if (deg[s2] === 2) order.push(s2);

    for (var oi = 0; oi < order.length; oi++) {
      var nd = order[oi];
      for (var t2 = 0; t2 < nb[nd].length; t2++) {
        var to = nb[nd][t2];
        if (usedEdge[ek(nd, to)]) continue;
        usedEdge[ek(nd, to)] = 1;
        var chain = extend(to, nd);
        if (chain.length >= 2) chains.push(chain);
      }
    }

    var out = [];
    for (var c = 0; c < chains.length; c++) {
      var ch = chains[c];
      var xy = [];
      for (var m2 = 0; m2 < ch.length; m2++) {
        var pi = pts[ch[m2]], ix = pi % w, iy = (pi - ix) / w;
        xy.push([ix * mpp, -iy * mpp]);
      }
      var simp = simplify(xy, 1.2);
      if (simp.length < 2) continue;
      var L = 0;
      for (var n2 = 1; n2 < simp.length; n2++) {
        L += Math.hypot(simp[n2][0] - simp[n2 - 1][0], simp[n2][1] - simp[n2 - 1][1]);
      }
      if (L < 10) continue;
      /* 半宽：链上各骨架点的距离变换值取平均，乘 2 就是这条路的像素宽度 */
      var hw = 0;
      if (dt) {
        var sm = 0;
        for (var m3 = 0; m3 < ch.length; m3++) sm += dt[pts[ch[m3]]];
        hw = sm / ch.length;
      }
      out.push({ path: simp, L: L, hw: hw });
    }
    return out;
  }


  /* ---------------------------------------------------------------- 主流程 */
  var K = 111319.49;
  function mkConv(lng0, lat0) {
    var kx = K * Math.cos(lat0 * Math.PI / 180);
    return {
      toM: function (lng, lat) { return [(lng - lng0) * kx, (lat - lat0) * K]; },
      toLL: function (x, y) { return [lng0 + x / kx, lat0 + y / K]; }
    };
  }

  // 2x2 降采样（骨架化对像素数最敏感，4000² 直接算会卡死）
  function half(m, w, h) {
    var w2 = w >> 1, h2 = h >> 1, out = new Uint8Array(w2 * h2);
    for (var y = 0; y < h2; y++) {
      for (var x = 0; x < w2; x++) {
        var i = (y * 2) * w + x * 2;
        out[y * w2 + x] = (m[i] + m[i + 1] + m[i + w] + m[i + w + 1]) >= 2 ? 1 : 0;
      }
    }
    return { m: out, w: w2, h: h2 };
  }

  function resize(m, w, h, tw) {
    var th = Math.max(1, Math.round(h * tw / w));
    var out = new Uint8Array(tw * th);
    for (var y = 0; y < th; y++) {
      var sy = Math.min(h - 1, Math.floor(y * h / th));
      for (var x = 0; x < tw; x++) {
        var sx = Math.min(w - 1, Math.floor(x * w / tw));
        out[y * tw + x] = m[sy * w + sx];
      }
    }
    return { m: out, w: tw, h: th };
  }

  /* 最大值降采样：一个采样块里只要有一个前景像素就保留。
     普通最近邻抽点会把 1~2 px 宽的支路整条抽掉，正是"支路全不见"的来源。 */
  function resizeMax(m, w, h, tw) {
    var th = Math.max(1, Math.round(h * tw / w));
    var out = new Uint8Array(tw * th);
    for (var y = 0; y < th; y++) {
      var sy0 = Math.floor(y * h / th), sy1 = Math.max(sy0 + 1, Math.floor((y + 1) * h / th));
      for (var x = 0; x < tw; x++) {
        var sx0 = Math.floor(x * w / tw), sx1 = Math.max(sx0 + 1, Math.floor((x + 1) * w / tw));
        var v = 0;
        for (var yy = sy0; yy < sy1 && !v; yy++) {
          var row = yy * w;
          for (var xx = sx0; xx < sx1; xx++) if (m[row + xx]) { v = 1; break; }
        }
        out[y * tw + x] = v;
      }
    }
    return { m: out, w: tw, h: th };
  }

  function pm(fn, ms) {
    return new Promise(function (res) {
      var done = false;
      function fin(v) { if (!done) { done = true; res(v); } }
      setTimeout(function () { fin(null); }, ms || 10000);
      try { fn(fin); } catch (e) { fin(null); }
    });
  }

  /* 绿地 / 水系：style=7 里 水=#ADD2FF 绿=#CFE8A7
     掩膜先降到 1000 px 再做轮廓追踪 —— Moore 追踪是逐像素走的，
     2184² 要跑七八秒，而绿地水面根本不需要 1 m 的边界精度。 */
  function polys(g, isWater, conv, minArea) {
    var m = maskFrom(g, isWater
      ? function (r, gg, b) { return b > 215 && b - r > 45 && b >= gg - 5; }
      : function (r, gg, b) { return gg > 215 && gg - b > 45 && gg - r > 15 && r < 226; });
    m = open(m, g.w, g.h, 1);

    var mm = m, pw = g.w, ph = g.h, pmpp = g.mpp;
    if (pw > 1000) {
      var rz = resize(m, g.w, g.h, 1000);
      pmpp = g.mpp * g.w / rz.w;
      mm = rz.m; pw = rz.w; ph = rz.h;
    }
    var minPx = Math.max(4, Math.round(minArea / (pmpp * pmpp)));
    mm = dropSmall(mm, pw, ph, minPx);

    var cs = traceContours(mm, pw, ph, 8);
    var out = [];
    for (var i = 0; i < cs.length; i++) {
      var c = cs[i];
      var sp = simplify(c, Math.max(1, 1.6 / pmpp));
      if (sp.length < 4) continue;
      var a = Math.abs(ringArea(sp)) * pmpp * pmpp;
      if (a < minArea) continue;
      var ring = [];
      for (var j = 0; j < sp.length; j++) {
        var lng = pxToLng(g.x0 + sp[j][0] * (g.w / pw), g.z);
        var lat = pxToLat(g.y0 + sp[j][1] * (g.h / ph), g.z);
        ring.push([+lng.toFixed(6), +lat.toFixed(6)]);
      }
      out.push({ ring: ring, area: Math.round(a) });
    }
    return out;
  }

  /* 从道路掩膜中移除连续同色的大面，避免骨架穿过广场和院落。 */
  function cutAreas(m, w, h, winPx, thr) {
    var I = new Int32Array((w + 1) * (h + 1));
    for (var y = 0; y < h; y++) {
      var row = 0;
      for (var x = 0; x < w; x++) {
        if (m[y * w + x]) row++;
        I[(y + 1) * (w + 1) + (x + 1)] = I[y * (w + 1) + (x + 1)] + row;
      }
    }
    var r = Math.max(1, winPx >> 1), out = new Uint8Array(m.length);
    for (var yy = 0; yy < h; yy++) {
      var y0 = Math.max(0, yy - r), y1 = Math.min(h, yy + r + 1);
      for (var xx = 0; xx < w; xx++) {
        if (!m[yy * w + xx]) continue;
        var x0 = Math.max(0, xx - r), x1 = Math.min(w, xx + r + 1);
        var sum = I[y1 * (w + 1) + x1] - I[y0 * (w + 1) + x1] -
                  I[y1 * (w + 1) + x0] + I[y0 * (w + 1) + x0];
        if (sum / ((y1 - y0) * (x1 - x0)) <= thr) out[yy * w + xx] = 1;
      }
    }
    return out;
  }

  /* Douglas-Peucker 折线简化。骨架路径是逐像素走出来的，转角天然带锯齿，
     直接连出来"发抖"；2.5 m 容差几乎不改走向，一条 200 m 的街能从几十个点
     降到 3~5 个，画面上就是笔直的街道。 */
  function dpSimplify(pts, tol) {
    var n = pts.length;
    if (n < 3) return pts;
    var keep = new Uint8Array(n);
    keep[0] = keep[n - 1] = 1;
    var stack = [[0, n - 1]];
    while (stack.length) {
      var seg = stack.pop(), i0 = seg[0], i1 = seg[1];
      var ax = pts[i0][0], ay = pts[i0][1], bx = pts[i1][0], by = pts[i1][1];
      var dx = bx - ax, dy = by - ay, dd = dx * dx + dy * dy;
      var best = -1, bi = -1;
      for (var q = i0 + 1; q < i1; q++) {
        var px = pts[q][0], py = pts[q][1], tt = 0;
        if (dd > 0) tt = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / dd));
        var ex = ax + tt * dx - px, ey = ay + tt * dy - py;
        var d2 = ex * ex + ey * ey;
        if (d2 > best) { best = d2; bi = q; }
      }
      if (best > tol * tol && bi > 0) {
        keep[bi] = 1;
        stack.push([i0, bi]);
        stack.push([bi, i1]);
      }
    }
    var o = [];
    for (var w = 0; w < n; w++) if (keep[w]) o.push(pts[w]);
    return o;
  }

  /* 道路：style=8 瓦片 */
  function roads(g, conv) {
    /* style=8 瓦片的路面色只有两类：黄的主干道 (240,220,101)，
       浅灰的次干/支路 (222,222,209)；建筑与地面填充是 (241,239,233)，
       占整块瓦片的 69%。三者 r-b 分别是 139 / 13 / 8 —— 靠阈值分不干净，
       阈值一松就把整个建成区收进来，骨架在填充块里长出一张假路网。
       所以只留一个宽阈值（把灰色支路放进来），真正的区分交给两步形态学：
         · 3x3 开运算削掉 1~2 px 的细边（建筑轮廓、树木阴影），
         · 距离变换的 hw（半宽）把"大片填充"剔掉（hw>9 ≈ 宽 55 m 以上）。
       这样既能保住支路，又不会把建筑当成路。 */
    /* 用绿色与红色通道的差值排除绿地，避免绿带被识别为道路。 */
    var m = maskFrom(g, function (r, gg, b, a) {
      return a > 200 && r >= 203 && gg >= 200 && gg <= r + 4 && (r - b) >= 9;
    });
    /* 分辨率是"道路不连续"的第一杀手：
         原来压到 760 px 覆盖 4 km，1 px ≈ 5.3 m，一条 6 m 宽的支路只剩 1.1 px，
         骨架化直接把它吃干净，只剩几条主干，看着就是一片断头路。
       现在用「最大值降采样」到 1320 px（1 px ≈ 3 m），支路还有 2 px，
       而且降采样时只要有前景像素就保留，不会像最近邻那样整条抽掉。 */
    /* 1600 px 覆盖 4 km → 1 px ≈ 2.5 m。比 1320 更细一档，6~10 m 的支路
       从"1 px 勉强够"变成"2~4 px 稳得住"，2 km 视野下路网才不至于只剩主干。 */
    var mpp2 = g.mpp, TARGET = 1600, hs = { m: m, w: g.w, h: g.h };
    if (hs.w > TARGET) {
      var sz = hs.w;
      hs = resizeMax(m, g.w, g.h, TARGET);
      mpp2 = g.mpp * sz / TARGET;
    }
    /* 挖掉广场/院落这类成片面：40 m 窗口、占比 >55% 判为面 */
    hs.m = cutAreas(hs.m, hs.w, hs.h, Math.max(3, Math.round(40 / mpp2)), 0.55);
    /* 仅在有噪点时使用开运算，以保留狭窄路段。 */
    var fg = 0;
    for (var fz = 0; fz < hs.m.length; fz++) if (hs.m[fz]) fg++;
    var dirty = fg / (hs.w * hs.h) > 0.12;
    if (window.__sbLog) window.__sbLog.push('road fg ' + (100 * fg / (hs.w * hs.h)).toFixed(1) + '% dirty=' + dirty);
    if (dirty) hs.m = open(hs.m, hs.w, hs.h, 1);
    /* 闭运算（膨胀→腐蚀）：路名压在路面上挖掉的那几个白字洞、斑马线、
       桥下阴影都会让掩膜断开，膨胀 2 px（≈6 m）先桥过去，再腐蚀回原宽度。 */
    hs.m = erode(dilate(hs.m, hs.w, hs.h, 2), hs.w, hs.h, 2);
    hs.m = dropSmall(hs.m, hs.w, hs.h, 20);

    var dt = distTransform(hs.m, hs.w, hs.h);
    var sk = skeletonize(hs.m, hs.w, hs.h);
    var chains = traceSkeleton(sk, hs.w, hs.h, mpp2, dt);

    // 像素 → 米（以图像中心为原点，y 向上）
    var halfPx = hs.w / 2;
    var segs = [];
    for (var i = 0; i < chains.length; i++) {
      var ch = chains[i];
      /* hw 是半宽像素（1 px ≈ 2.5 m），两头都要卡：
         · <0.85 等于路面不到 4 m 宽 —— 园路、树影、建筑轮廓的残余，
           全画出来整张图会"发毛"，这也是实时路网看着比缓存数据乱的主因；
         · >9（宽 55 m 以上）是整片填充区的骨架，不是路。 */
      if (ch.hw < 1.05 || ch.hw > 9) continue;
      var xy = [], pts = [];
      for (var j = 0; j < ch.path.length; j++) {
        var mx = +(ch.path[j][0] - halfPx * mpp2).toFixed(1);
        var my = +(ch.path[j][1] + halfPx * mpp2).toFixed(1);
        xy.push(mx, my);
        pts.push([mx, my]);
      }
      var wM = Math.min(24, Math.max(6.8, ch.hw * 2 * mpp2));
      segs.push({ pts: dpSimplify(pts, 2.5), w: Math.round(wM) });
    }

    /* 端点吸附补缝：掩膜在路口、桥洞、颜色渐变处断开，骨架也就断在那里。
       把距离近、走向又接得上的两个端点用短线连起来。阈值比高速严得多
       （120 m / cos>0.52），免得把平行的两条街误连成一条。 */
    function dirOf(p, atEnd) {
      var n = p.length;
      var a = atEnd ? p[n - 1] : p[0];
      var b = atEnd ? p[Math.max(0, n - 4)] : p[Math.min(n - 1, 3)];
      var vx = a[0] - b[0], vy = a[1] - b[1], L = Math.hypot(vx, vy);
      return L > 1e-9 ? [vx / L, vy / L] : [1, 0];
    }
    var ends = [];
    for (var a0 = 0; a0 < segs.length; a0++) {
      var sp0 = segs[a0].pts;
      if (sp0.length < 2) continue;
      ends.push({ i: a0, p: sp0[0], d: dirOf(sp0, false), w: segs[a0].w });
      ends.push({ i: a0, p: sp0[sp0.length - 1], d: dirOf(sp0, true), w: segs[a0].w });
    }
    var pairs = [];
    for (var x0 = 0; x0 < ends.length; x0++) {
      for (var y0 = x0 + 1; y0 < ends.length; y0++) {
        if (ends[x0].i === ends[y0].i) continue;
        var A = ends[x0], B = ends[y0];
        var dd = Math.hypot(B.p[0] - A.p[0], B.p[1] - A.p[1]);
        if (dd < 6 || dd > 55) continue;
        var ux = (B.p[0] - A.p[0]) / dd, uy = (B.p[1] - A.p[1]) / dd;
        var c1 = ux * A.d[0] + uy * A.d[1];
        var c2 = -(ux * B.d[0] + uy * B.d[1]);
        if (c1 < 0.78 || c2 < 0.78) continue;
        pairs.push([dd - (c1 + c2) * 30, dd, x0, y0]);
      }
    }
    pairs.sort(function (p1, p2) { return p1[0] - p2[0]; });
    var used = {}, links = [];
    for (var q0 = 0; q0 < pairs.length; q0++) {
      var pa = pairs[q0];
      if (used[pa[2]] || used[pa[3]]) continue;
      used[pa[2]] = used[pa[3]] = 1;
      links.push({ xy: [ends[pa[2]].p[0], ends[pa[2]].p[1], ends[pa[3]].p[0], ends[pa[3]].p[1]],
                   w: Math.min(ends[pa[2]].w, ends[pa[3]].w), L: Math.round(pa[1]) });
    }

    var out = [];
    for (var r0 = 0; r0 < segs.length; r0++) {
      var pth = segs[r0].pts, L = 0, flat = [];
      for (var s0 = 0; s0 < pth.length; s0++) {
        flat.push(pth[s0][0], pth[s0][1]);
        if (s0) L += Math.hypot(pth[s0][0] - pth[s0 - 1][0], pth[s0][1] - pth[s0 - 1][1]);
      }
      /* 12 m 以上就画 = 骨架每根毛刺都留着。35 m 以下的多半是路口切出来的
         碎片和掩膜残余，删掉之后路网立刻干净一档。 */
      if (flat.length < 4 || L < 35) continue;
      out.push({ xy: flat, w: segs[r0].w, L: Math.round(L) });
    }
    for (var l0 = 0; l0 < links.length; l0++) out.push(links[l0]);
    return out;
  }

  /* 高速公路：style=7 橙色（#FFA35C 系）
     z12 下一条 30 m 宽的立交只有 1 px，骨架化必然碎；原来还先把图压到
     460 px（1 px≈130 m），路直接被抹掉 —— 这就是"高速不连续"的全部原因。
     现在的做法：
       降到 1024 → 膨胀 2 px（≈120 m）桥接断口 → 取骨架 → 端点吸附补缝。
     骨架给出的是中心线，线宽由页面按固定像素画，所以任何视野都细而连续；
     直接在原图上填面则会在 5 km 视野里变成一坨坨色块（试过）。 */
  function express(g, conv) {
    var m = maskFrom(g, function (r, gg, b) {
      return r > 238 && gg > 96 && gg < 190 && b > 26 && b < 130;
    });
    var pix0 = 0;
    for (var pz = 0; pz < m.length; pz++) if (m[pz]) pix0++;
    if (window.__sbLog) window.__sbLog.push('exp mask ' + pix0 + '/' + m.length +
      ' @' + g.w + 'px ' + g.mpp.toFixed(2) + 'm/px');
    var mm = m, ww = g.w, hh = g.h, mppX = g.mpp;
    /* 降采样必须用「最大值」而不是最近邻。
       最近邻按 1706/1400 的比例丢整行整列，一条 4 px 宽的高速带会被抽成
       断续的点，骨架化出来 9419 条碎链 —— 这是实时路径"高速全断"的真正原因，
       道路那边早就在用 resizeMax，只有高速这里漏了。 */
    if (ww > 1400) {
      var rz = resizeMax(m, ww, hh, 1400);
      mppX = g.mpp * ww / 1400;
      mm = rz.m; ww = rz.w; hh = rz.h;
    }
    /* 闭运算（先膨胀再腐蚀）：把路名压出来的豁口桥上，但不把整条带子撑宽。
       半径千万别超过 7：z11 下高速带只有 4 px 宽，腐蚀 8 px 会把它啃成
       1 px 细线（实测距离变换最大值直接掉到 1.0），后面所有按宽度判断的
       逻辑都会失真。6 px 是"桥得动豁口、又留得住带子"的那个点。 */
    mm = erode(dilate(mm, ww, hh, 6), ww, hh, 6);
    mm = dropSmall(mm, ww, hh, Math.max(4, Math.round(6000 / (mppX * mppX))));

    var pix1 = 0;
    for (var pz2 = 0; pz2 < mm.length; pz2++) if (mm[pz2]) pix1++;
    if (window.__sbLog) window.__sbLog.push('exp morph ' + pix1 + ' @' + ww + 'px ' +
      mppX.toFixed(1) + 'm/px drop=' + Math.max(4, Math.round(6000 / (mppX * mppX))));
    var sk = skeletonize(mm, ww, hh);
    var chains = traceSkeleton(sk, ww, hh, mppX, null);
    if (window.__sbLog) window.__sbLog.push('exp chains ' + chains.length);
    var halfPx = ww / 2;

    var segs = [];
    for (var i = 0; i < chains.length; i++) {
      var ch = chains[i];
      if (ch.L < 200) continue;
      var pts = [];
      for (var j = 0; j < ch.path.length; j++) {
        pts.push([ch.path[j][0] - halfPx * mppX, ch.path[j][1] + halfPx * mppX]);
      }
      if (pts.length >= 2) segs.push(pts);
    }

    /* 端点吸附：立交、跨线桥、隧道口在掩膜上是断的，骨架也跟着断。
       把两端够近、方向又接得上的端点用一条短直接上。 */
    function dirOf(p, atEnd) {
      var n = p.length;
      var a = atEnd ? p[n - 1] : p[0];
      var b = atEnd ? p[Math.max(0, n - 5)] : p[Math.min(n - 1, 4)];
      var vx = a[0] - b[0], vy = a[1] - b[1], L = Math.hypot(vx, vy);
      return L > 1e-9 ? [vx / L, vy / L] : [1, 0];
    }
    var ends = [];
    for (var a0 = 0; a0 < segs.length; a0++) {
      ends.push({ i: a0, e: 0, p: segs[a0][0], d: dirOf(segs[a0], false) });
      ends.push({ i: a0, e: 1, p: segs[a0][segs[a0].length - 1], d: dirOf(segs[a0], true) });
    }
    var used = {}, links = [];
    var pairs = [];
    for (var x0 = 0; x0 < ends.length; x0++) {
      for (var y0 = x0 + 1; y0 < ends.length; y0++) {
        if (ends[x0].i === ends[y0].i) continue;
        var A = ends[x0], B = ends[y0];
        var dd = Math.hypot(B.p[0] - A.p[0], B.p[1] - A.p[1]);
        /* 距离放到 5500 m、夹角余弦放到 0.18：50 km 视野下 1 px ≈ 71 m，
           骨架端点本来就是锯齿状的，方向估计误差大，卡 0.30 会把
           "其实该连"的端点判成接不上。 */
        if (dd < 30 || dd > 5500) continue;
        var ux = (B.p[0] - A.p[0]) / dd, uy = (B.p[1] - A.p[1]) / dd;
        var c1 = ux * A.d[0] + uy * A.d[1];
        var c2 = -(ux * B.d[0] + uy * B.d[1]);
        if (c1 < 0.18 || c2 < 0.18) continue;
        pairs.push([dd - (c1 + c2) * 120, dd, x0, y0]);
      }
    }
    pairs.sort(function (p1, p2) { return p1[0] - p2[0]; });
    for (var k0 = 0; k0 < pairs.length; k0++) {
      var pa = pairs[k0];
      if (used[pa[2]] || used[pa[3]]) continue;
      used[pa[2]] = used[pa[3]] = 1;
      links.push([ends[pa[2]].p, ends[pa[3]].p]);
    }
    for (var l0 = 0; l0 < links.length; l0++) segs.push(links[l0]);

    var out = [];
    for (var q0 = 0; q0 < segs.length; q0++) {
      var pth = segs[q0], L = 0;
      for (var r0 = 1; r0 < pth.length; r0++) {
        L += Math.hypot(pth[r0][0] - pth[r0 - 1][0], pth[r0][1] - pth[r0 - 1][1]);
      }
      var ring = [];
      for (var t0 = 0; t0 < pth.length; t0++) {
        var ll = conv.toLL(pth[t0][0], pth[t0][1]);
        ring.push([+ll[0].toFixed(6), +ll[1].toFixed(6)]);
      }
      out.push({ name: '', path: ring, L: Math.round(L) });
    }

    /* 悬空短线：骨架毛刺、被立交切断的一小截，画出来就是一段没有去向的
       短线头，比断口还难看。两端 600 m 内都找不到别的段端点、长度又不到
       900 m 的，直接丢掉。 */
    if (out.length > 1) {
      var cLat = pxToLat(g.y0 + g.h / 2, g.z);
      var KX2 = 111319.49 * Math.cos(cLat * Math.PI / 180), KY2 = 111319.49;
      var ends2 = [];
      for (var u0 = 0; u0 < out.length; u0++) {
        var pp0 = out[u0].path;
        ends2.push({ i: u0, p: pp0[0] });
        ends2.push({ i: u0, p: pp0[pp0.length - 1] });
      }
      var kept = [];
      for (var u1 = 0; u1 < out.length; u1++) {
        var e1 = out[u1];
        if (e1.L >= 900) { kept.push(e1); continue; }
        var a1 = e1.path[0], b1 = e1.path[e1.path.length - 1];
        var na = false, nb2 = false;
        for (var u2 = 0; u2 < ends2.length; u2++) {
          if (ends2[u2].i === u1) continue;
          var q2 = ends2[u2].p;
          if (!na && Math.hypot((a1[0] - q2[0]) * KX2, (a1[1] - q2[1]) * KY2) < 600) na = true;
          if (!nb2 && Math.hypot((b1[0] - q2[0]) * KX2, (b1[1] - q2[1]) * KY2) < 600) nb2 = true;
          if (na && nb2) break;
        }
        if (na && nb2) kept.push(e1);
      }
      out = kept;
    }
    return out;
  }

  /* ---------------------------------------------------------------- POI */
  /* 单次检索：失败/超时回 null，由外面的 poiSearch 负责退避重试，
     并发由 gate() 统一控制。 */
  function poiSearchOnce(type, lng, lat, radius, keyword, page) {
    return new Promise(function (res) {
      var done = false;
      function fin(v) { if (!done) { done = true; res(v); } }
      var timer = setTimeout(function () {
        if (window.__sbLog) window.__sbLog.push('poi ' + type + ' TIMEOUT');
        fin(null);
      }, 15000);
      try {
        var ps = new AMap.PlaceSearch({
          type: type, pageSize: 25, pageIndex: page || 1,
          city: '全国', citylimit: false, extensions: 'base'
        });
        ps.searchNearBy(keyword || '', [lng, lat], radius, function (st, result) {
          clearTimeout(timer);
          if (st === 'complete' && result && result.poiList && result.poiList.pois) {
            fin(result.poiList.pois.map(function (p) {
              return {
                name: p.name,
                lng: p.location.lng, lat: p.location.lat,
                dist: Math.round(p.distance || 0),
                type: p.type || ''
              };
            }));
          } else {
            if (window.__sbLog) window.__sbLog.push('poi ' + type + ' => ' + st);
            fin(null);
          }
        });
      } catch (e) {
        clearTimeout(timer);
        fin(null);
      }
    });
  }

  /* 高德 POI 并发一高就回 error（一次发 6 个必挂几个）。原来只重试一次、
     隔 350 ms，实测"火车站"这类大半径检索经常连挂两次，最后 2 km 内一个
     地铁站都搜不出来。改成最多三次，间隔 400 / 1000 ms 递增。 */
  function poiSearch(type, lng, lat, radius, keyword) {
    function attempt(n) {
      return poiSearchOnce(type, lng, lat, radius, keyword).then(function (v) {
        if (v) return v;
        if (n >= 2) return [];
        return new Promise(function (r) { setTimeout(r, 400 * (n + 1)); })
          .then(function () { return attempt(n + 1); });
      });
    }
    return attempt(0);
  }

  /* 分页获取候选并逐页重试，避免站内设施占满结果。 */
  function poiSearchPaged(type, lng, lat, radius, pages, keyword) {
    var out = [], i = 1;
    function one(pg, n) {
      return poiSearchOnce(type, lng, lat, radius, keyword, pg).then(function (v) {
        if (v) return v;
        if (n >= 2) return [];
        return new Promise(function (r) { setTimeout(r, 400 * (n + 1)); })
          .then(function () { return one(pg, n + 1); });
      });
    }
    function step() {
      if (i > (pages || 1)) return Promise.resolve(out);
      var pg = i++;
      return one(pg, 0).then(function (v) {
        if (v && v.length) out = out.concat(v);
        return step();
      });
    }
    return step();
  }

  /* 并发闸门：最多 n 个同时在飞 */
  function gate(n) {
    var q = [], active = 0;
    function pump() {
      while (active < n && q.length) {
        active++;
        var job = q.shift();
        job.fn().then(function (v) { active--; job.ok(v); pump(); },
                      function () { active--; job.ok([]); pump(); });
      }
    }
    return function (fn) {
      return new Promise(function (ok) { q.push({ fn: fn, ok: ok }); pump(); });
    };
  }

  /* 高德的 type 形如「交通设施服务;火车站;火车站」，最后一段才是具体类型。 */
  function subType(t) {
    var a = (t || '').split(';');
    return a.length >= 3 ? a[2] : (a[a.length - 1] || '');
  }
  function normName(n) {
    return (n || '').replace(/[（(][^）)]*[）)]/g, '').replace(/\s+/g, '').trim();
  }
  /* 根据设施子类型筛选客运站本体。 */
  function cutAt(n, key) {
    var i = (n || '').indexOf(key);
    return i >= 0 ? n.slice(0, i + key.length) : (n || '');
  }

  /* ---------------------------------------------------------- 坐标加偏 */
  /* 将 OSM 的 WGS84 坐标转换为底图使用的 GCJ02，再叠加图形。 */
  var PI = Math.PI, SEMI_A = 6378245.0, EE = 0.00669342162296594323;

  function outOfChina(lng, lat) {
    return !(lng > 73.66 && lng < 135.05 && lat > 3.86 && lat < 53.55);
  }
  function tLat(x, y) {
    var r = -100.0 + 2.0 * x + 3.0 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.sqrt(Math.abs(x));
    r += (20.0 * Math.sin(6.0 * x * PI) + 20.0 * Math.sin(2.0 * x * PI)) * 2.0 / 3.0;
    r += (20.0 * Math.sin(y * PI) + 40.0 * Math.sin(y / 3.0 * PI)) * 2.0 / 3.0;
    r += (160.0 * Math.sin(y / 12.0 * PI) + 320 * Math.sin(y * PI / 30.0)) * 2.0 / 3.0;
    return r;
  }
  function tLng(x, y) {
    var r = 300.0 + x + 2.0 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.sqrt(Math.abs(x));
    r += (20.0 * Math.sin(6.0 * x * PI) + 20.0 * Math.sin(2.0 * x * PI)) * 2.0 / 3.0;
    r += (20.0 * Math.sin(x * PI) + 40.0 * Math.sin(x / 3.0 * PI)) * 2.0 / 3.0;
    r += (150.0 * Math.sin(x / 12.0 * PI) + 300.0 * Math.sin(x / 30.0 * PI)) * 2.0 / 3.0;
    return r;
  }
  function wgs2gcj(lng, lat) {
    if (outOfChina(lng, lat)) return [lng, lat];
    var dLat = tLat(lng - 105.0, lat - 35.0);
    var dLng = tLng(lng - 105.0, lat - 35.0);
    var rad = lat / 180.0 * PI;
    var magic = Math.sin(rad);
    magic = 1 - EE * magic * magic;
    var sm = Math.sqrt(magic);
    dLat = (dLat * 180.0) / ((SEMI_A * (1 - EE)) / (magic * sm) * PI);
    dLng = (dLng * 180.0) / (SEMI_A / sm * Math.cos(rad) * PI);
    return [lng + dLng, lat + dLat];
  }

  /* GCJ-02 → WGS-84（正向公式迭代反解，3 轮就收敛到厘米级）。
     Overpass 的 bbox 是 WGS-84；拿 GCJ 的中心去框，查询区会整体偏
     300~500 m，2 km 视野就有一边缺口。 */
  function gcj2wgs(lng, lat) {
    if (outOfChina(lng, lat)) return [lng, lat];
    var wl = lng, wa = lat;
    for (var i = 0; i < 4; i++) {
      var g = wgs2gcj(wl, wa);
      wl -= g[0] - lng;
      wa -= g[1] - lat;
    }
    return [wl, wa];
  }

  /* ------------------------------------------------------ OSM 高速矢量 */
  /* 区域尺度下优先以道路中心线连接路段，低分辨率栅格不足以稳定提取骨架。 */
  var EXP_DROP = /高架|快速路|跨线桥|立交/;

  /* 启用 trunk 时同时查询城市快速路。 */
  function packExpress(elements, lng, lat, half, opts) {
    var keepTrunk = !!(opts && opts.trunk);
    if (!elements || !elements.length) return [];
    var groups = {}, order = [];
    for (var i = 0; i < elements.length; i++) {
      var e = elements[i];
      if (e.type !== 'way' || !e.geometry || e.geometry.length < 2) continue;
      var tg = e.tags || {};
      var nm = ((tg.name || tg.ref || '') + '').trim();
      if (!keepTrunk && nm && EXP_DROP.test(nm)) continue;
      var g = [];
      for (var j = 0; j < e.geometry.length; j++) {
        var q0 = e.geometry[j];
        if (!isFinite(q0.lon) || !isFinite(q0.lat)) { g = null; break; }
        g.push([q0.lon, q0.lat]);
      }
      if (!g || g.length < 2) continue;
      if (!groups[nm]) { groups[nm] = []; order.push(nm); }
      groups[nm].push({ n: (e.nodes || []).slice(), g: g });
    }

    return finishExpress(raw, lng, lat, half, 1200);
  }

  /* 收尾：加偏 → 裁剪 → 抽稀到 15 m → 同名接缝。
     stitchM = 两条同名链端点相距多远以内就接上。
     OSM（Overpass）在立交/跨江/隧道口本来就是断的，给 1200 m；
     矢量瓦片本身连续，只需要补瓦片边界的小缝，给 90 m。 */
  function finishExpress(raw, lng, lat, half, stitchM, minLen) {
    var MIN = minLen || 300;
    var KX = 111319.49 * Math.cos(lat * Math.PI / 180), KY = 111319.49;
    function mx(q) { return (q[0] - lng) * KX; }
    function my(q) { return (q[1] - lat) * KY; }
    function dist(a, b) { return Math.hypot(mx(a) - mx(b), my(a) - my(b)); }

    /* 加偏（OSM 是 WGS-84）→ 按半径裁剪 → 抽稀到 15 m。
       抽稀只是去掉节点级冗余，形状不变；不抽稀 50 km 范围内会有几万个点。 */
    var res = [];
    for (var ri = 0; ri < raw.length; ri++) {
      var ln = raw[ri], run = [], pts = ln.pts;
      for (var pi = 0; pi < pts.length; pi++) {
        var gc = wgs2gcj(pts[pi][0], pts[pi][1]);
        if (Math.hypot(mx(gc), my(gc)) <= half) {
          run.push(gc);
        } else if (run.length) {
          if (run.length >= 2) res.push({ name: ln.name, pts: run });
          run = [];
        }
      }
      if (run.length >= 2) res.push({ name: ln.name, pts: run });
    }

    var out = [];
    for (var si = 0; si < res.length; si++) {
      var src = res[si].pts, simp = [src[0]];
      for (var ti = 1; ti < src.length; ti++) {
        if (ti === src.length - 1 || dist(src[ti], simp[simp.length - 1]) >= 15) simp.push(src[ti]);
      }
      if (simp.length < 2) continue;
      var L = 0;
      for (var li = 1; li < simp.length; li++) L += dist(simp[li], simp[li - 1]);
      if (L < MIN) continue;
      out.push({ name: res[si].name, path: simp.map(function (q) {
        return [+q[0].toFixed(6), +q[1].toFixed(6)]; }), L: Math.round(L) });
    }

    /* 同名接缝：共享节点的段已经串好了，剩下的缺口在立交、跨江桥、隧道口，
       两条同名链的端点离得不远就直接连上 —— 不连的话外环永远是豁的。
       1200 m 以内才连，再大就是两条不同的路，硬连会拉出一条假线。 */
    /* 一直合到没有可合的为止（原来写死 6 轮，3173 条碎段连 6 次根本不够）。
       加一个 6000 次的上限兜底，避免数据异常时死循环。 */
    for (var round = 0, guard = 0; stitchM > 0 && round < 6000; round++) {
      var merged = false;
      for (var x = 0; x < out.length && !merged; x++) {
        for (var y = x + 1; y < out.length && !merged; y++) {
          if (out[x].name !== out[y].name) continue;
          var A = out[x].path, B = out[y].path;
          var combos = [
            [A[A.length - 1], B[0], 1, 1], [A[A.length - 1], B[B.length - 1], 1, -1],
            [A[0], B[0], -1, 1], [A[0], B[B.length - 1], -1, -1]
          ];
          var best = null;
          for (var cb = 0; cb < combos.length; cb++) {
            var dd = dist(combos[cb][0], combos[cb][1]);
            if (dd <= (stitchM || 1200) && (!best || dd < best[0])) best = [dd, combos[cb]];
          }
          if (!best) continue;
          var pa = best[1][2] === 1 ? A.slice() : A.slice().reverse();
          var pb = best[1][3] === 1 ? B.slice() : B.slice().reverse();
          out[x].path = pa.concat(pb);
          out[x].L = out[x].L + out[y].L + Math.round(best[0]);
          out.splice(y, 1);
          merged = true;
        }
      }
      if (!merged) break;
    }
    out.sort(function (a, b) { return b.L - a.L; });
    return out;
  }


  /* ------------------------------------------ 高速主源：OpenFreeMap 矢量瓦片 */
  /* Overpass 的 50 km 查询高峰期要 100~200 s、还常 502，换个城市就是碰运气。
     OpenFreeMap 是 OSM 的矢量瓦片 CDN（无需 key、任意坐标、带 CORS），
     z11 下 100 km 见方约 30 张瓦片、几秒拿全，几何本身就是连续中心线。
     瓦片里 class=motorway / trunk 和 OSM 的 highway 口径一致，ramp（匝道）剔掉，
     跟原来 osmExpress 的 ^motorway$ 完全对得上。 */
  var TILEJSON_URL = 'https://tiles.openfreemap.org/planet';
  var tileTplCache = null;      /* 只是本次会话的 URL 模板，不落盘、不跟坐标绑定 */

  function tileTemplate() {
    if (tileTplCache) return Promise.resolve(tileTplCache);
    return fetch(TILEJSON_URL, { cache: 'no-store' }).then(function (r) {
      if (!r.ok) throw new Error('TileJSON HTTP ' + r.status);
      return r.json();
    }).then(function (j) {
      var t = (j && j.tiles && j.tiles[0]) || '';
      if (!t) throw new Error('TileJSON 里没有 tiles');
      tileTplCache = t;
      return t;
    });
  }

  /* 瓦片是 gzip 过的裸 pbf（响应头没带 Content-Encoding，浏览器不会自动解） */
  function gunzipBytes(u8) {
    if (u8.length < 2 || u8[0] !== 0x1f || u8[1] !== 0x8b) return Promise.resolve(u8);
    if (typeof DecompressionStream === 'undefined') {
      return Promise.reject(new Error('浏览器不支持 DecompressionStream'));
    }
    var st = new Blob([u8]).stream().pipeThrough(new DecompressionStream('gzip'));
    return new Response(st).arrayBuffer().then(function (b) { return new Uint8Array(b); });
  }

  function mvtVarint(u8, i, end) {
    var r = 0, sh = 0, b;
    do {
      if (i >= end) throw new Error('varint 越界');
      b = u8[i++];
      r += (b & 0x7f) * Math.pow(2, sh);
      sh += 7;
    } while (b & 0x80);
    return [r, i];
  }

  /* 返回 [[field, wiretype, value], ...]；wiretype=2 时 value 是 [start, end) */
  function mvtFields(u8, start, end) {
    var out = [], i = start;
    while (i < end) {
      var r = mvtVarint(u8, i, end); i = r[1];
      var f = Math.floor(r[0] / 8), t = r[0] & 7, v;
      if (t === 0) {
        var r0 = mvtVarint(u8, i, end); i = r0[1]; v = r0[0];
      } else if (t === 2) {
        var r2 = mvtVarint(u8, i, end); i = r2[1];
        v = [i, i + r2[0]]; i += r2[0];
      } else if (t === 5) {
        v = [i, i + 4]; i += 4;
      } else if (t === 1) {
        v = [i, i + 8]; i += 8;
      } else {
        break;                       /* 不认识的 wiretype，放弃这层 */
      }
      out.push([f, t, v]);
    }
    return out;
  }

  function mvtStr(u8, r) {
    var a = new Uint8Array(u8.subarray(r[0], r[1])), s2 = '';
    for (var i = 0; i < a.length; i++) s2 += '%' + (a[i] < 16 ? '0' : '') + a[i].toString(16);
    return decodeURIComponent(s2);
  }

  function mvtValue(u8, r) {
    var fs = mvtFields(u8, r[0], r[1]);
    for (var i = 0; i < fs.length; i++) {
      var f = fs[i][0], t = fs[i][1], v = fs[i][2];
      if (f === 1) return mvtStr(u8, v);
      if (f === 3) return t === 1 ? new DataView(u8.buffer, u8.byteOffset + v[0], 8).getFloat64(0, true) : 0;
      if (f === 4) return t === 0 ? v : 0;
      if (f === 5) return t === 0 ? v : 0;
      if (f === 7) return !!(t === 0 ? v : 0);
    }
    return null;
  }

  function tileLL(z, tx, ty, px, py, extent) {
    var n = Math.pow(2, z);
    var lng = (tx + px / extent) / n * 360 - 180;
    var t2 = (ty + py / extent) / n;
    var lat = Math.atan(Math.sinh(Math.PI * (1 - 2 * t2))) * 180 / Math.PI;
    return [lng, lat];
  }

  function mvtGeom(u8, start, end, z, tx, ty, extent) {
    var lines = [], cur = null, cx = 0, cy = 0, i = start;
    while (i < end) {
      var r = mvtVarint(u8, i, end); i = r[1];
      var cmd = r[0] & 7, count = Math.floor(r[0] / 8);
      if (cmd === 7) {                                  /* ClosePath：面要素才有 */
        if (cur && cur.length > 1) lines.push(cur);
        cur = null;
        continue;
      }
      for (var k = 0; k < count; k++) {
        var a = mvtVarint(u8, i, end); i = a[1];
        var b = mvtVarint(u8, i, end); i = b[1];
        cx += (a[0] >> 1) ^ (-(a[0] & 1));              /* zigzag */
        cy += (b[0] >> 1) ^ (-(b[0] & 1));
        if (cmd === 1 && k === 0) {                     /* MoveTo：新的一段 */
          if (cur && cur.length > 1) lines.push(cur);
          cur = [];
        }
        if (!cur) cur = [];
        cur.push(tileLL(z, tx, ty, cx, cy, extent));
      }
    }
    if (cur && cur.length > 1) lines.push(cur);
    return lines;
  }

  /* 解析一张瓦片：只要 transportation 图层里的 motorway/trunk 线。
     路名不在这一层（在 transportation_name 里，且顶点未必对得上），
     高速标注继续用 build 阶段从高德 POI 算出来的 expLabels。 */
  function readTile(u8, z, tx, ty, trunkOn, segs) {
    var top = mvtFields(u8, 0, u8.length);
    for (var i = 0; i < top.length; i++) {
      if (top[i][0] !== 3) continue;
      var lf = mvtFields(u8, top[i][2][0], top[i][2][1]);
      var lname = '', extent = 4096, keys = [], vals = [], feats = [];
      for (var j = 0; j < lf.length; j++) {
        var f = lf[j][0], v = lf[j][2];
        if (f === 1) lname = mvtStr(u8, v);
        else if (f === 5) extent = v;
        else if (f === 3) keys.push(mvtStr(u8, v));
        else if (f === 4) vals.push(mvtValue(u8, v));
        else if (f === 2) feats.push(v);
      }
      if (lname !== 'transportation') continue;

      for (var fi = 0; fi < feats.length; fi++) {
        var tags = {}, type = 0, geom = null;
        var ff = mvtFields(u8, feats[fi][0], feats[fi][1]);
        for (var k = 0; k < ff.length; k++) {
          var kf = ff[k][0], kt = ff[k][1], kv = ff[k][2];
          if (kf === 2 && kt === 2) {
            var nums = [], p2 = kv[0];
            while (p2 < kv[1]) { var r2 = mvtVarint(u8, p2, kv[1]); p2 = r2[1]; nums.push(r2[0]); }
            for (var q2 = 0; q2 + 1 < nums.length; q2 += 2) {
              var kk = keys[nums[q2]];
              if (kk !== undefined) tags[kk] = vals[nums[q2 + 1]];
            }
          } else if (kf === 3) type = kv;
          else if (kf === 4) geom = kv;
        }
        if (!geom || type !== 2) continue;               /* 只要线 */
        var cls = tags['class'] || '';
        if (cls.indexOf('construction') >= 0) continue;
        if (!(cls === 'motorway' || (trunkOn && cls === 'trunk'))) continue;
        /* 匝道不要：MVT 里的布尔值解出来是 true，不是 1 */
        if (tags.ramp === true || tags.ramp === 1 || tags.ramp === '1') continue;
        var nm = (tags.name || tags.ref || '') + '';
        var lines = mvtGeom(u8, geom[0], geom[1], z, tx, ty, extent);
        for (var l2 = 0; l2 < lines.length; l2++) {
          if (lines[l2].length >= 2) segs.push({ pts: lines[l2], name: nm });
        }
      }
    }
  }

  /* 串链：瓦片是 z11（一格约 4 m），相邻瓦片在边界上的裁切点会差几米，
     所以不能按"坐标完全相等"接，用 33 m 网格 + 45 m 容差找最近的端点。
     立交处会有三四条端点挤在同一个点上（主线和匝道），只按距离选会接错方向、
     接出一根根"毛刺"，所以再按**延续方向**打分：转弯越大罚得越重。 */
  /* opts: cell 网格边长(度) / tol 端点容差(米) / prune 剪掉悬空短段 / spur 剪掉链首尾毛刺。
     高速用默认值（33 m 网格、45 m 容差、两样都剪）；
     街道传 prune:false —— 断头路本身是事实，不能当成毛刺删掉。 */
  function chainTiles(segs, opts) {
    var OPT = opts || {};
    var CELL = OPT.cell || 0.0003;         /* 默认约 33 m */
    var TOLM = OPT.tol == null ? 45 : OPT.tol;
    var PRUNE = OPT.prune !== false;
    var SPUR = OPT.spur !== false;
    var COSMIN = OPT.cosMin == null ? -1 : OPT.cosMin;
    var PERPM = OPT.perp == null ? 1e9 : OPT.perp;   /* 垂直偏移上限（米） */
    var grid = {}, i;
    function ck(p) { return Math.floor(p[0] / CELL) + ',' + Math.floor(p[1] / CELL); }
    function add(idx, p) {
      var k = ck(p);
      (grid[k] = grid[k] || []).push(idx);
    }
    for (i = 0; i < segs.length; i++) {
      add(i, segs[i].pts[0]);
      add(i, segs[i].pts[segs[i].pts.length - 1]);
    }

    /* 预剪枝：一端悬空、长度 600 m 以内的段，就是立交匝道/收费站在图上那一根根
       小毛刺。串链会把它们接到主线上变成链首的"尾巴"，靠后面按长度过滤不掉，
       所以在串链前先剔掉。 */
    var used = new Array(segs.length), chains = [];
    function hasNeighbor(idx, end) {
      var pts = segs[idx].pts;
      var p = end ? pts[pts.length - 1] : pts[0];
      var cs = Math.cos(p[1] * Math.PI / 180);
      var cx = Math.floor(p[0] / CELL), cy = Math.floor(p[1] / CELL);
      for (var gx = -1; gx <= 1; gx++) {
        for (var gy = -1; gy <= 1; gy++) {
          var list = grid[(cx + gx) + ',' + (cy + gy)];
          if (!list) continue;
          for (var q = 0; q < list.length; q++) {
            var oi = list[q];
            if (oi === idx || used[oi]) continue;
            var op = segs[oi].pts;
            for (var e2 = 0; e2 < 2; e2++) {
              var q2 = e2 ? op[op.length - 1] : op[0];
              if (Math.hypot((q2[0] - p[0]) * cs, q2[1] - p[1]) * 111319 <= TOLM) return true;
            }
          }
        }
      }
      return false;
    }
    /* 跑三轮：一条 300 m 的匝道常被瓦片切成两段，先剪掉末段，下一轮前段才"悬空" */
    for (var pass = 0; PRUNE && pass < 3; pass++) {
      for (i = 0; i < segs.length; i++) {
        if (used[i]) continue;
        var pv = segs[i].pts;
        var a0 = hasNeighbor(i, 0), a1 = hasNeighbor(i, 1);
        if (a0 && a1) continue;                  /* 两头都连着，是主线的一段 */
        var len = 0;
        for (var k2 = 1; k2 < pv.length; k2++) {
          len += Math.hypot((pv[k2][0] - pv[k2 - 1][0]) * Math.cos(pv[0][1] * Math.PI / 180),
                            pv[k2][1] - pv[k2 - 1][1]) * 111319;
        }
        if (len < 600) used[i] = 1;              /* 悬空又短 → 毛刺，剔掉 */
      }
    }

    /* dx/dy 是行进方向的单位向量；返回最合适的接续段与接哪一端 */
    function near(p, dx, dy) {
      var cx = Math.floor(p[0] / CELL), cy = Math.floor(p[1] / CELL);
      var cs = Math.cos(p[1] * Math.PI / 180);
      var best = -1, bs = 1e18, bEnd = 0;
      for (var gx = -1; gx <= 1; gx++) {
        for (var gy = -1; gy <= 1; gy++) {
          var list = grid[(cx + gx) + ',' + (cy + gy)];
          if (!list) continue;
          for (var q = 0; q < list.length; q++) {
            var si = list[q];
            if (used[si]) continue;
            var pts = segs[si].pts, n = pts.length;
            /* 两个端点各算一次：距离 + 方向惩罚 */
            var ends = [[pts[0], pts[1], 0], [pts[n - 1], pts[n - 2], 1]];
            for (var e = 0; e < 2; e++) {
              var ep = ends[e][0], np = ends[e][1];
              var vx = (np[0] - ep[0]) * cs, vy = np[1] - ep[1];
              var vl = Math.hypot(vx, vy) || 1;
              var dm = Math.hypot((ep[0] - p[0]) * cs, ep[1] - p[1]) * 111319;
              if (dm > TOLM) continue;
              var cosv = (vx / vl) * dx + (vy / vl) * dy;
              /* 方向门槛：只接大致同向的。容差放大到几十米以后，
                 不加这道门槛就会把旁边的另一条街当成续接，接出一条斜穿街区的假路。 */
              if (cosv < COSMIN) continue;
              /* 还要"在正前方、几乎共线"：把位移投影到行进方向上，
                 沿向投影必须非负（在后方的不接），垂直偏移必须很小。
                 只按欧氏距离 + 方向选，会把平行相邻的另一条街接过来
                 —— 那就是图上那条斜穿街区的长直线。 */
              if (PERPM < 1e8) {
                var ddx = (ep[0] - p[0]) * cs, ddy = ep[1] - p[1];
                var tA = ddx * dx + ddy * dy;                 /* 沿向（度） */
                var tP = Math.abs(ddy * dx - ddx * dy);       /* 横向（度） */
                if (tA < -0.0002) continue;
                if (tP * 111319 > PERPM) continue;
              }
              var score = dm + (1 - cosv) * 40;      /* 掉头 180° 相当于多跑 80 m */
              if (score < bs) { bs = score; best = si; bEnd = ends[e][2]; }
            }
          }
        }
      }
      if (best < 0) return null;
      return { i: best, end: bEnd };
    }

    for (i = 0; i < segs.length; i++) {
      if (used[i]) continue;
      used[i] = 1;
      var pts = segs[i].pts.slice();
      var cs2 = Math.cos(pts[0][1] * Math.PI / 180);
      for (;;) {                                          /* 往后接 */
        var n0 = pts.length, ax = 0, ay = 0;
        if (n0 >= 2) {
          var vx0 = (pts[n0 - 1][0] - pts[n0 - 2][0]) * cs2;
          var vy0 = pts[n0 - 1][1] - pts[n0 - 2][1];
          var l0 = Math.hypot(vx0, vy0) || 1;
          ax = vx0 / l0; ay = vy0 / l0;
        }
        var nx = near(pts[n0 - 1], ax, ay);
        if (!nx) break;
        used[nx.i] = 1;
        var pn = segs[nx.i].pts;
        pts = nx.end === 0 ? pts.concat(pn.slice(1)) : pts.concat(pn.slice(0, -1).reverse());
        if (pts.length > 30000) break;
      }
      for (;;) {                                          /* 往前接 */
        var bx = 0, by = 0;
        if (pts.length >= 2) {
          var vx1 = (pts[0][0] - pts[1][0]) * cs2;
          var vy1 = pts[0][1] - pts[1][1];
          var l1 = Math.hypot(vx1, vy1) || 1;
          bx = vx1 / l1; by = vy1 / l1;
        }
        var nv = near(pts[0], bx, by);
        if (!nv) break;
        used[nv.i] = 1;
        var pp = segs[nv.i].pts;
        pts = nv.end === 1 ? pp.slice(0, -1).concat(pts) : pp.slice(1).reverse().concat(pts);
        if (pts.length > 30000) break;
      }
      /* 链首/链尾的短毛刺：第一段不足 350 m 又拐了 50° 以上，基本是匝道口，
         直接从链上剪掉（它不影响主线的前后连通）。 */
      if (SPUR) { trimSpur(pts, false); trimSpur(pts, true); }
      chains.push({ name: segs[i].name, pts: pts, w: segs[i].w, tier: segs[i].tier });
    }
    return chains;
  }

  function trimSpur(pts, fromEnd) {
    for (var guard = 0; guard < 40 && pts.length > 3; guard++) {
      var p0, p1, p2;
      if (fromEnd) { p0 = pts[pts.length - 1]; p1 = pts[pts.length - 2]; p2 = pts[pts.length - 3]; }
      else { p0 = pts[0]; p1 = pts[1]; p2 = pts[2]; }
      var cs = Math.cos(p0[1] * Math.PI / 180);
      var d01 = Math.hypot((p1[0] - p0[0]) * cs, p1[1] - p0[1]) * 111319;
      if (d01 > 350) return;
      var a1 = Math.atan2((p1[1] - p0[1]), (p1[0] - p0[0]) * cs);
      var a2 = Math.atan2((p2[1] - p1[1]), (p2[0] - p1[0]) * cs);
      var dd = Math.abs(((a1 - a2 + Math.PI * 3) % (Math.PI * 2)) - Math.PI);
      if (dd < 50 * Math.PI / 180) return;
      if (fromEnd) pts.pop(); else pts.shift();
    }
  }

  /* 去掉挂在主线中段的短横刺。
     源数据在 z11 把主线和匝道都切成小段：主线串成一条长链之后，匝道常常独立成链，
     一端悬空、另一端正好搭在主线中段 —— 图上就是一根根 100~300 m 的小尾巴。
     它们既不是断头路（确实有连接），也不在链首尾（trimSpur 剪不到），
     靠长度过滤也过不掉（长度是按"链"算的，不是按"尾巴"算的）。
     判据：链长 < 300 m，且有一端落在某条 ≥800 m 长链的**中段**（离两头都 >8 个点）。 */
  function dropStubs(chains) {
    var M = 111319.49, i, j, k;
    function segLen(pts) {
      var t = 0;
      for (var q = 1; q < pts.length; q++) {
        var cs = Math.cos(pts[q][1] * Math.PI / 180);
        t += Math.hypot((pts[q][0] - pts[q - 1][0]) * cs, pts[q][1] - pts[q - 1][1]) * M;
      }
      return t;
    }
    var info = [];
    for (i = 0; i < chains.length; i++) {
      var pts0 = chains[i].pts;
      info.push({ L: segLen(pts0), a: pts0[0], b: pts0[pts0.length - 1] });
    }
    var out = [];
    for (i = 0; i < chains.length; i++) {
      if (info[i].L >= 300) { out.push(chains[i]); continue; }   /* 够长，不动 */
      var stub = false;
      for (var e = 0; e < 2 && !stub; e++) {
        var p = e ? info[i].b : info[i].a;
        var cs2 = Math.cos(p[1] * Math.PI / 180);
        for (j = 0; j < chains.length; j++) {
          if (j === i || info[j].L < 800) continue;              /* 只认"主线" */
          var q2 = chains[j].pts, best = 1e9, bi = -1;
          for (k = 0; k < q2.length; k++) {
            var d = Math.hypot((q2[k][0] - p[0]) * cs2, q2[k][1] - p[1]) * M;
            if (d < best) { best = d; bi = k; }
          }
          if (best > 25) continue;
          /* 落点不能在主线两头：那是同一条路被切成两段，接上就对了，不能删 */
          if (bi < 3 || bi > q2.length - 4) continue;
          stub = true;
          break;
        }
      }
      if (!stub) out.push(chains[i]);
    }
    if (window.__sbLog && out.length !== chains.length) {
      window.__sbLog.push('express 剪掉 ' + (chains.length - out.length) + ' 根短横刺');
    }
    return out;
  }

  function packTileExpress(tiles, z, lng, lat, half, trunkOn) {
    var segs = [];
    for (var i = 0; i < tiles.length; i++) {
      try {
        readTile(tiles[i].data, z, tiles[i].x, tiles[i].y, trunkOn, segs);
      } catch (e) { /* 单张瓦片坏了不影响其它 */ }
    }
    if (!segs.length) return [];
    /* stitchM=0：瓦片已经用 45 m 容差串过链了，再跑一遍 O(n²) 的同名接缝
       又慢又会在立交处乱连。minLen=1500：高速上短于 1.5 km 的独立段基本是
       收费站/立交的分岔（图上就是一根根小毛刺），不画。
       dropStubs 再补一刀：串进主线里、但端头搭在主线中段的短支。 */
    var ch = dropStubs(chainTiles(segs));
    return finishExpress(ch, lng, lat, half, 0, 1500);
  }

  /* 取 50 km 内的 motorway 中心线。任意坐标——不分城市、不预取、不缓存。 */
  /* 批量取瓦片：并发 6，超时只是提前收工（已拿到的照用），单张失败不重试。
     高速（z11×49 张）和街道（z14×9 张）都走这一条。 */
  function fetchTiles(want, Z, timeoutMs) {
    return tileTemplate().then(function (tpl) {
      return new Promise(function (resolve) {
        var out = [], idx = 0, active = 0, done = false;
        var deadline = setTimeout(function () {
          if (!done) { done = true; resolve(out); }
        }, timeoutMs || 45000);
        function next() {
          if (done) return;
          if (idx >= want.length && active === 0) {
            done = true; clearTimeout(deadline); resolve(out); return;
          }
          while (active < 6 && idx < want.length) {
            (function (t) {
              active++;
              var url = tpl.replace('{z}', Z).replace('{x}', t.x).replace('{y}', t.y);
              fetch(url, { cache: 'force-cache' }).then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.arrayBuffer();
              }).then(function (b) {
                return gunzipBytes(new Uint8Array(b));
              }).then(function (u8) {
                out.push({ x: t.x, y: t.y, data: u8 });
              }).catch(function () {}).then(function () {
                active--; next();
              });
            })(want[idx++]);
          }
        }
        next();
      });
    });
  }

  function osmExpressTiles(lng, lat, half) {
    var Z = 11, n = Math.pow(2, Z);
    var trunkOn = !!(window.SITE_CONFIG && window.SITE_CONFIG.express &&
                     window.SITE_CONFIG.express.trunk);
    var wg = gcj2wgs(lng, lat);
    var dLat = half / 111319.49;
    var dLng = half / (111319.49 * Math.cos(wg[1] * Math.PI / 180));
    function xOf(lo) { return (lo + 180) / 360 * n; }
    function yOf(la) {
      var r = la * Math.PI / 180;
      return (1 - Math.log(Math.tan(r) + 1 / Math.cos(r)) / Math.PI) / 2 * n;
    }
    var x0 = Math.floor(xOf(wg[0] - dLng)), x1 = Math.floor(xOf(wg[0] + dLng));
    var y0 = Math.floor(yOf(wg[1] + dLat)), y1 = Math.floor(yOf(wg[1] - dLat));
    var want = [];
    for (var x = x0; x <= x1; x++) for (var y = y0; y <= y1; y++) want.push({ x: x, y: y });

    return fetchTiles(want, Z, 45000).then(function (tiles) {
      if (window.__sbLog) window.__sbLog.push('express tiles 到手 ' + tiles.length + '/' + want.length);
      if (!tiles.length) throw new Error('瓦片一张都没拿到');
      var tt0 = Date.now();
      var list = packTileExpress(tiles, Z, lng, lat, half, trunkOn);
      if (window.__sbLog) window.__sbLog.push('express pack ' + (Date.now() - tt0) + ' ms');
      if (window.__sbLog) window.__sbLog.push('express tiles ' + list.length + ' 段 / ' +
        Math.round(list.reduce(function (a, b) { return a + b.L; }, 0) / 1000) + ' km / ' +
        tiles.length + '/' + want.length + ' 张');
      return list;
    });
  }

  /* -------------------------------------------------------- OSM 街道中心线 */
  /* 优先使用 OSM 矢量中心线，避免瓦片骨架将双向道路或广场误识别为多条街道。 */
  /* 同时纳入 trunk 和 primary，避免漏掉主要街道。 */
  var ROAD_CLS = '(trunk|primary|secondary|tertiary|residential|unclassified|living_street|pedestrian)(_link)?';
  var ROAD_WID = {
    trunk: 26, trunk_link: 22,
    primary: 22, primary_link: 20,
    secondary: 13, secondary_link: 11,
    tertiary: 9.5, tertiary_link: 8,
    residential: 7.5, unclassified: 7.5,
    living_street: 6, pedestrian: 6
  };
  /* 三档线宽靠 w 分：≥20 主干、≥10 次干、其余支路（见页面 prepare 里的 lv） */
  var ROAD_TIER = { trunk: 0, trunk_link: 0, primary: 0, primary_link: 0,
                    secondary: 1, secondary_link: 1,
                    tertiary: 2, tertiary_link: 2 };
  var ROAD_HW = new RegExp('^' + ROAD_CLS + '$');
  /* 街道图层过滤隧道、高架、匝道和立交引道。 */
  var ROAD_DROP = /隧道|通道|高架|快速路|跨线桥|立交|匝道|出口|入口/;
  /* 两个道路后缀连续出现时，过滤作为路口名称的组合条目。 */
  function isCrossName(nm) {
    var m = (nm || '').match(/大道|大街|公路|胡同|路|街|道|巷|弄/g);
    return !!m && m.length >= 2;
  }

  /* ============================ 街道主源：OpenFreeMap 矢量瓦片 ==========
     Overpass 的街道查询要 60~120 s、还常常 502；高德瓦片骨架只能给形状、没有路名，
     而且 1~2 px 的路面色带细化出来天生接不干净（就是"道路断裂"的来源）。
     OpenFreeMap 是 OSM 的矢量瓦片 CDN：4.3 km 见方在 z14 只有 9 张、3~5 s，
     几何精度 0.6 m。同一条 OSM way 跨瓦片被裁开的两段在 buffer 里带重叠，
     端点坐标完全一致（1e-6° 级），串起来就是连续的一条 —— 没有断口。
     z14 是 OpenFreeMap 的最高级别，正是街道需要的精度。 */
  var ROAD_TILE_Z = 14;

  /* MVT class → [路宽(米), 档位]。tier 只用于路名标注的优先级；
     实际线宽由页面按 路宽/mpp 换算（见 prepare 里的 lv）。 */
  var TILE_ROAD = {
    trunk: [24, 0], primary: [20, 0], secondary: [13, 1],
    tertiary: [9.5, 2], minor: [7.5, 2], service: [3.6, 2]
  };

  /* 瓦片经纬度矩形：[西, 南, 东, 北] */
  function tileBounds(z, tx, ty) {
    var n = Math.pow(2, z);
    function latOf(y) { return Math.atan(Math.sinh(Math.PI * (1 - 2 * y / n))) * 180 / Math.PI; }
    return [tx / n * 360 - 180, latOf(ty + 1), (tx + 1) / n * 360 - 180, latOf(ty)];
  }

  /* 线段与矩形求交，返回 [tIn, tOut]（参数 0~1），完全在外返回 null。
     折线裁剪靠这个逐段算入点/出点。 */
  function segRectT(a, b, r) {
    var t0 = 0, t1 = 1, dx = b[0] - a[0], dy = b[1] - a[1];
    var p = [-dx, dx, -dy, dy];
    var q = [a[0] - r[0], r[2] - a[0], a[1] - r[1], r[3] - a[1]];
    for (var i = 0; i < 4; i++) {
      if (p[i] === 0) { if (q[i] < 0) return null; continue; }
      var t = q[i] / p[i];
      if (p[i] < 0) { if (t > t1) return null; if (t > t0) t0 = t; }
      else { if (t < t0) return null; if (t < t1) t1 = t; }
    }
    return [t0, t1];
  }

  /* 把折线裁到瓦片矩形内（**不含 buffer**）。
     MVT 的几何会延出瓦片边界一个 buffer（z14 约 76 m），相邻瓦片在重叠区
     各留一份副本。不裁的话，串链只能靠放大容差去接 150 m 外的端点，
     结果是误连出一条条斜穿街区的假路；裁到核心区后，两条瓦片在边界上的
     交点坐标一致（同一段几何、同一条边界），2 m 容差就能严丝合缝接上。 */
  function clipPolyline(pts, r) {
    var out = [], run = [], i;
    function inside(p) { return p[0] >= r[0] && p[0] <= r[2] && p[1] >= r[1] && p[1] <= r[3]; }
    for (i = 0; i < pts.length; i++) {
      var p = pts[i];
      if (inside(p)) {
        if (!run.length && i > 0) {
          /* 从框外进框内：取**入点** tIn（e[0]）。取 e[1] 会跑到线段另一端，
             裁出来的线整条错位 —— 上一版"接了但没接上"就是这里写反了。 */
          var e = segRectT(pts[i - 1], p, r);
          if (e) run.push([pts[i - 1][0] + (p[0] - pts[i - 1][0]) * e[0],
                           pts[i - 1][1] + (p[1] - pts[i - 1][1]) * e[0]]);
        }
        run.push(p);
      } else if (run.length) {
        var e2 = segRectT(pts[i - 1], p, r);
        if (e2) {
          run.push([pts[i - 1][0] + (p[0] - pts[i - 1][0]) * e2[1],
                    pts[i - 1][1] + (p[1] - pts[i - 1][1]) * e2[1]]);
        }
        if (run.length >= 2) out.push(run);
        run = [];
      }
    }
    if (run.length >= 2) out.push(run);
    return out;
  }

  function readTileRoads(u8, z, tx, ty, segs, namePts) {
    var top = mvtFields(u8, 0, u8.length);
    for (var i = 0; i < top.length; i++) {
      if (top[i][0] !== 3) continue;
      var lf = mvtFields(u8, top[i][2][0], top[i][2][1]);
      var lname = '', extent = 4096, keys = [], vals = [], feats = [];
      for (var j = 0; j < lf.length; j++) {
        var f = lf[j][0], v = lf[j][2];
        if (f === 1) lname = mvtStr(u8, v);
        else if (f === 5) extent = v;
        else if (f === 3) keys.push(mvtStr(u8, v));
        else if (f === 4) vals.push(mvtValue(u8, v));
        else if (f === 2) feats.push(v);
      }
      if (lname !== 'transportation' && lname !== 'transportation_name') continue;

      for (var fi = 0; fi < feats.length; fi++) {
        var tags = {}, type = 0, geom = null;
        var ff = mvtFields(u8, feats[fi][0], feats[fi][1]);
        for (var k = 0; k < ff.length; k++) {
          var kf = ff[k][0], kt = ff[k][1], kv = ff[k][2];
          if (kf === 2 && kt === 2) {
            var nums = [], p2 = kv[0];
            while (p2 < kv[1]) { var r2 = mvtVarint(u8, p2, kv[1]); p2 = r2[1]; nums.push(r2[0]); }
            for (var q2 = 0; q2 + 1 < nums.length; q2 += 2) {
              var kk = keys[nums[q2]];
              if (kk !== undefined) tags[kk] = vals[nums[q2 + 1]];
            }
          } else if (kf === 3) type = kv;
          else if (kf === 4) geom = kv;
        }
        if (!geom || type !== 2) continue;

        var cls = (tags['class'] || '') + '';

        /* 保留正式路名及对应道路的顶点，供后续定位标注。 */
        if (lname === 'transportation_name') {
          var nm0 = ((tags.name || tags.ref || '') + '').replace(/\s+/g, '').trim();
          if (!nm0 || nm0.length < 2) continue;
          var nl = mvtGeom(u8, geom[0], geom[1], z, tx, ty, extent);
          for (var li = 0; li < nl.length; li++) {
            for (var vi = 0; vi < nl[li].length; vi++) {
              namePts.push({ name: nm0, lng: nl[li][vi][0], lat: nl[li][vi][1] });
            }
          }
          continue;
        }

        var spec = TILE_ROAD[cls];
        if (!spec) continue;                       /* rail / transit / path 不要 */
        if (tags.brunnel === 'tunnel') continue;   /* 隧道在地下，卫星图上本来就看不见 */
        if (tags.ramp === true || tags.ramp === 1 || tags.ramp === '1') continue;
        var lines = mvtGeom(u8, geom[0], geom[1], z, tx, ty, extent);
        var rb = tileBounds(z, tx, ty);
        for (var l2 = 0; l2 < lines.length; l2++) {
          var cl = clipPolyline(lines[l2], rb);
          for (var l3 = 0; l3 < cl.length; l3++) {
            if (cl[l3].length >= 2) segs.push({ pts: cl[l3], w: spec[0], tier: spec[1] });
          }
        }
      }
    }
  }

  /* 把「WGS-84 折线 + 路宽 + 档位 + 名字」装箱成页面用的道路线。
     两种数据源（Overpass / 矢量瓦片）共用这一段，保证成图一致：
     出框裁剪 → 抽稀 1.4 m → 短于 12 m 的丢掉 → 按名字汇总标注点。 */
  function packRoadList(list, lng, lat, half, cv, KX, KY, extNames) {
    var lines = [], byName = {}, order = [];
    for (var i = 0; i < list.length; i++) {
      var it = list[i], src = it.pts, w = it.w || 7.5;
      var tier = it.tier == null ? 3 : it.tier, nm = it.name || '';
      var run = [];
      for (var j = 0; j <= src.length; j++) {
        var q0 = j < src.length ? src[j] : null;
        var ok = false, mx = 0, my = 0;
        if (q0 && isFinite(q0[0]) && isFinite(q0[1])) {
          var gc = wgs2gcj(q0[0], q0[1]);            /* OSM 是 WGS-84，底图是 GCJ-02 */
          mx = (gc[0] - lng) * KX;
          my = (gc[1] - lat) * KY;
          ok = Math.abs(mx) <= half && Math.abs(my) <= half;
        }
        if (ok) { run.push([mx, my]); continue; }
        if (run.length >= 2) {
          var simp = dpSimplify(run, 1.4), L = 0, xy = [];
          for (var k = 0; k < simp.length; k++) {
            xy.push(+simp[k][0].toFixed(1), +simp[k][1].toFixed(1));
            if (k) L += Math.hypot(simp[k][0] - simp[k - 1][0], simp[k][1] - simp[k - 1][1]);
          }
          if (L >= 12) {
            lines.push({ xy: xy, w: w, L: Math.round(L), tier: tier, name: nm });
            if (nm) {
              var a0 = byName[nm];
              if (!a0) { a0 = byName[nm] = { name: nm, tier: 9, nd: 1e9, bd: 1e9, best: null }; order.push(nm); }
              if (tier < a0.tier) a0.tier = tier;
              for (var m1 = 0; m1 < simp.length; m1++) {
                var dd = Math.hypot(simp[m1][0], simp[m1][1]);
                if (dd < a0.nd) a0.nd = dd;
                /* 标在离中心 320 m 左右的位置：名字沿路撒在场地四周，
                   不会全挤在项目基地边上；路本身就在脚下的（<90 m）不标名字。 */
                var sc = dd < 90 ? 1e6 - dd : Math.abs(dd - 320);
                if (sc < a0.bd) { a0.bd = sc; a0.best = simp[m1]; }
              }
            }
          }
        }
        run = [];
      }
    }
    /* 按距场地每 500 m 分档，档内优先显示主干道。 */
    /* 外部给标注点（矢量瓦片的 transportation_name 顶点）：直接按名字取最近的那个点。
       位置就在它自己的路上，不存在"挂错街"的问题。 */
    var names = [];
    if (extNames && extNames.length) {
      var near = {}, seen2 = [], e2;
      for (e2 = 0; e2 < extNames.length; e2++) {
        var t2 = extNames[e2], n2 = t2.name;
        if (!n2) continue;
        /* 将瓦片坐标从 WGS84 转为 GCJ02，再比较各路名标注点到基地的距离。 */
        var g2 = wgs2gcj(t2.lng, t2.lat);
        var mx2 = (g2[0] - lng) * KX, my2 = (g2[1] - lat) * KY;
        var d2 = Math.hypot(mx2, my2);
        if (d2 > half) continue;
        var cur = near[n2];
        if (!cur) { near[n2] = { name: n2, lng: g2[0], lat: g2[1], nd: d2 }; seen2.push(n2); }
        else if (d2 < cur.nd) { cur.lng = g2[0]; cur.lat = g2[1]; cur.nd = d2; }
      }
      var cand2 = [];
      for (e2 = 0; e2 < seen2.length; e2++) {
        var cn = near[seen2[e2]];
        if (ROAD_DROP.test(cn.name) || cn.name.length < 2 ||
            /桥$/.test(cn.name) || isCrossName(cn.name)) continue;
        cand2.push(cn);
      }
      cand2.sort(function (p1, p2) {
        var b1 = Math.min(3, Math.floor(p1.nd / 500));
        var b2 = Math.min(3, Math.floor(p2.nd / 500));
        return (b1 - b2) || (p1.nd - p2.nd);
      });
      for (e2 = 0; e2 < cand2.length && names.length < 22; e2++) {
        names.push({ name: cand2[e2].name, lng: cand2[e2].lng, lat: cand2[e2].lat });
      }
      return { lines: lines, names: names };
    }

    function bucket(nm0) { return Math.min(3, Math.floor(byName[nm0].nd / 500)); }
    order.sort(function (p1, p2) {
      return (bucket(p1) - bucket(p2)) ||
             (byName[p1].tier - byName[p2].tier) ||
             (byName[p1].nd - byName[p2].nd);
    });
    for (var oi = 0; oi < order.length && names.length < 22; oi++) {
      var a1 = byName[order[oi]];
      if (!a1.best) continue;
      var ll = cv.toLL(a1.best[0], a1.best[1]);
      names.push({ name: a1.name, lng: ll[0], lat: ll[1] });
    }
    return { lines: lines, names: names };
  }

  function packTileRoads(tiles, z, lng, lat, half) {
    var segs = [], namePts = [];
    for (var i = 0; i < tiles.length; i++) {
      try {
        readTileRoads(tiles[i].data, z, tiles[i].x, tiles[i].y, segs, namePts);
      } catch (e) { /* 单张瓦片坏了不影响其它 */ }
    }
    if (!segs.length) return null;
    /* 道路不做"悬空短段"剪枝：断头路是事实，不是毛刺。
       z14 的瓦片接缝是精确的，容差 10 m 足够（网格 6.6 m）。 */
    /* 瓦片在边界处是**按顶点**切断的（不插交点），相邻瓦片各留一段，
       中间的真实缺口实测 10~50 m，所以沿向容差给 40 m。
       能放这么宽是因为同时限定了三条：方向 cos ≥ 0.6、必须在正前方（沿向投影 ≥ 0）、
       横向偏移 ≤ 9 m —— 只接"顺着同一条线接着走"的那段。
       不做剪枝：断头路是事实，不是毛刺。 */
    var chains = chainTiles(segs,
      { cell: 0.0005, tol: 40, cosMin: 0.6, perp: 9, prune: false, spur: false });
    /* 链上不挂名字：一条链可能跨两条街，挂谁的名字都不对。
       标注完全由 transportation_name 自己的点决定（见 packRoadList 的 extNames）。 */
    var list = [];
    for (var c = 0; c < chains.length; c++) {
      var ch = chains[c];
      list.push({ pts: ch.pts, w: ch.w || 7.5, tier: ch.tier == null ? 3 : ch.tier, name: '' });
    }
    var cv = mkConv(lng, lat);
    var KX = 111319.49 * Math.cos(lat * Math.PI / 180), KY = 111319.49;
    var out = packRoadList(list, lng, lat, half, cv, KX, KY, namePts);
    if (window.__sbLog) window.__sbLog.push('road tiles ' + out.lines.length + ' 段 / ' +
      Math.round(out.lines.reduce(function (a, b) { return a + b.L; }, 0) / 1000) +
      ' km / 链 ' + chains.length + ' / 名字点 ' + namePts.length +
      ' / names ' + out.names.length + ' / ' + tiles.length + ' 张');
    return out;
  }

  /* ===================== 水面 / 绿地：同一套矢量瓦片 =====================
     原来这两层是从高德 z16 栅格瓦片里按色带抠面域：一格 9.5 m，边缘是像素锯齿，
     再一开运算、一简化，成图上就是"碎块拼接"—— 水面和绿地都不连续。
     矢量面是真正的多边形，边界精度 0.6 m，而且不需要色带判断。 */
  var TILE_GREEN_LC = { grass: 1, wood: 1, wetland: 1 };
  var TILE_GREEN_LU = { park: 1, forest: 1, grass: 1, cemetery: 1, recreation_ground: 1,
                        village_green: 1, meadow: 1, orchard: 1, allotments: 1 };

  /* MVT 的 polygon 用 ClosePath 收尾，几何里没有"回到起点"那个点，补上 */
  function mvtRings(u8, start, end, z, tx, ty, extent) {
    var lines = mvtGeom(u8, start, end, z, tx, ty, extent), out = [];
    for (var i = 0; i < lines.length; i++) {
      var r = lines[i];
      if (r.length < 3) continue;
      var a = r[0], b = r[r.length - 1];
      if (a[0] !== b[0] || a[1] !== b[1]) r.push([a[0], a[1]]);
      if (r.length >= 4) out.push(r);
    }
    return out;
  }

  function readTilePolys(u8, z, tx, ty, wRings, gRings) {
    var top = mvtFields(u8, 0, u8.length);
    for (var i = 0; i < top.length; i++) {
      if (top[i][0] !== 3) continue;
      var lf = mvtFields(u8, top[i][2][0], top[i][2][1]);
      var lname = '', extent = 4096, keys = [], vals = [], feats = [];
      for (var j = 0; j < lf.length; j++) {
        var f = lf[j][0], v = lf[j][2];
        if (f === 1) lname = mvtStr(u8, v);
        else if (f === 5) extent = v;
        else if (f === 3) keys.push(mvtStr(u8, v));
        else if (f === 4) vals.push(mvtValue(u8, v));
        else if (f === 2) feats.push(v);
      }
      var isW = (lname === 'water');
      var isLC = (lname === 'landcover'), isLU = (lname === 'landuse'), isPK = (lname === 'park');
      if (!isW && !isLC && !isLU && !isPK) continue;

      for (var fi = 0; fi < feats.length; fi++) {
        var tags = {}, type = 0, geom = null;
        var ff = mvtFields(u8, feats[fi][0], feats[fi][1]);
        for (var k = 0; k < ff.length; k++) {
          var kf = ff[k][0], kt = ff[k][1], kv = ff[k][2];
          if (kf === 2 && kt === 2) {
            var nums = [], p2 = kv[0];
            while (p2 < kv[1]) { var r2 = mvtVarint(u8, p2, kv[1]); p2 = r2[1]; nums.push(r2[0]); }
            for (var q2 = 0; q2 + 1 < nums.length; q2 += 2) {
              var kk = keys[nums[q2]];
              if (kk !== undefined) tags[kk] = vals[nums[q2 + 1]];
            }
          } else if (kf === 3) type = kv;
          else if (kf === 4) geom = kv;
        }
        if (!geom || type !== 3) continue;                    /* 只要面 */
        var cls = (tags['class'] || '') + '';
        if (!isW) {
          if (isLC && !TILE_GREEN_LC[cls]) continue;
          if (isLU && !TILE_GREEN_LU[cls]) continue;
          /* park 图层整层都是绿地 */
        }
        var rings = mvtRings(u8, geom[0], geom[1], z, tx, ty, extent);
        var dst = isW ? wRings : gRings;
        for (var ri = 0; ri < rings.length; ri++) dst.push(rings[ri]);
      }
    }
  }

  /* 环面积（平方米）：经纬度先换米，再走鞋带公式 */
  function ringAreaM(ring, lat) {
    var KX = 111319.49 * Math.cos(lat * Math.PI / 180), KY = 111319.49, s = 0;
    var x0 = ring[0][0], y0 = ring[0][1];
    for (var i = 0; i < ring.length - 1; i++) {
      var x1 = (ring[i][0] - x0) * KX, y1 = (ring[i][1] - y0) * KY;
      var x2 = (ring[i + 1][0] - x0) * KX, y2 = (ring[i + 1][1] - y0) * KY;
      s += x1 * y2 - x2 * y1;
    }
    return Math.abs(s / 2);
  }

  function packTilePolys(tiles, z, lng, lat, half) {
    var wRings = [], gRings = [], i;
    for (i = 0; i < tiles.length; i++) {
      try { readTilePolys(tiles[i].data, z, tiles[i].x, tiles[i].y, wRings, gRings); }
      catch (e) { /* 单张瓦片坏了不影响其它 */ }
    }
    var KX = 111319.49 * Math.cos(lat * Math.PI / 180), KY = 111319.49;
    function conv(rings, minA) {
      var out = [];
      for (var ri = 0; ri < rings.length; ri++) {
        var r = rings[ri], g = [], cx = 0, cy = 0;
        for (var j = 0; j < r.length; j++) {
          var c = wgs2gcj(r[j][0], r[j][1]);
          g.push([+c[0].toFixed(6), +c[1].toFixed(6)]);
          cx += (c[0] - lng) * KX;
          cy += (c[1] - lat) * KY;
        }
        cx /= r.length; cy /= r.length;
        /* 面心离基地太远就丢：4 km 半径的框里一半是框外的 */
        if (Math.hypot(cx, cy) > half * 1.45) continue;
        var a = ringAreaM(g, lat);
        if (a < minA) continue;
        out.push({ ring: g, area: Math.round(a) });
      }
      return out;
    }
    var res = { water: conv(wRings, 1200), green: conv(gRings, 900) };
    if (window.__sbLog) window.__sbLog.push('poly tiles 水 ' + res.water.length +
      ' / 绿 ' + res.green.length + ' / ' + tiles.length + ' 张');
    return res;
  }

  /* ===================== 河道：waterway 线 =====================
     OSM 里城区河道绝大多数是 `waterway=canal/river` 的**线**，不是
     `natural=water` 的面。仅取水面会遗漏仅有中心线的河道。
     这里把线单独取出来，页面用「水系」面的同一个颜色画粗线 ——
     图层、图例、开关仍只有「水系」一项，不新增条目。 */
  var WATERWAY_W = { river: 30, canal: 18, stream: 8, ditch: 4, drain: 3 };
  var WATERWAY_TIER = { river: 0, canal: 1, stream: 2, ditch: 2, drain: 2 };
  var WATERWAY_CLS = /^(river|canal|stream|ditch|drain)$/;

  function readTileWaterways(u8, z, tx, ty, segs) {
    var top = mvtFields(u8, 0, u8.length);
    for (var i = 0; i < top.length; i++) {
      if (top[i][0] !== 3) continue;
      var lf = mvtFields(u8, top[i][2][0], top[i][2][1]);
      var lname = '', extent = 4096, keys = [], vals = [], feats = [];
      for (var j = 0; j < lf.length; j++) {
        var f = lf[j][0], v = lf[j][2];
        if (f === 1) lname = mvtStr(u8, v);
        else if (f === 5) extent = v;
        else if (f === 3) keys.push(mvtStr(u8, v));
        else if (f === 4) vals.push(mvtValue(u8, v));
        else if (f === 2) feats.push(v);
      }
      if (lname !== 'waterway') continue;

      for (var fi = 0; fi < feats.length; fi++) {
        var tags = {}, type = 0, geom = null;
        var ff = mvtFields(u8, feats[fi][0], feats[fi][1]);
        for (var k = 0; k < ff.length; k++) {
          var kf = ff[k][0], kt = ff[k][1], kv = ff[k][2];
          if (kf === 2 && kt === 2) {
            var nums = [], p2 = kv[0];
            while (p2 < kv[1]) { var r2 = mvtVarint(u8, p2, kv[1]); p2 = r2[1]; nums.push(r2[0]); }
            for (var q2 = 0; q2 + 1 < nums.length; q2 += 2) {
              var kk = keys[nums[q2]];
              if (kk !== undefined) tags[kk] = vals[nums[q2 + 1]];
            }
          } else if (kf === 3) type = kv;
          else if (kf === 4) geom = kv;
        }
        if (!geom || type !== 2) continue;                  /* 只要线 */
        var cls = (tags['class'] || '') + '';
        if (!WATERWAY_CLS.test(cls)) continue;
        if (tags.brunnel === 'tunnel') continue;            /* 地下暗河看不见 */
        /* 河宽：优先 OSM 的 width 标签（米），没有就按等级给个默认值。
           默认值仅用于示意河宽。 */
        var wm = parseFloat(tags.width);
        var measured = isFinite(wm) && wm > 0 ? wm : null;
        if (measured === null) wm = WATERWAY_W[cls] || 8;
        if (wm > 200) wm = 200;
        var nm = (tags.name || '') + '';
        var lines = mvtGeom(u8, geom[0], geom[1], z, tx, ty, extent);
        var rb = tileBounds(z, tx, ty);      /* 裁到核心区，避免相邻瓦片各画一遍 */
        for (var l2 = 0; l2 < lines.length; l2++) {
          var cl = clipPolyline(lines[l2], rb);
          for (var l3 = 0; l3 < cl.length; l3++) {
            if (cl[l3].length < 2) continue;
            segs.push({ pts: cl[l3], w: wm, measured: measured, cls: cls, name: nm });
          }
        }
      }
    }
  }

  /* 和 packRoadList 一样：WGS-84 → GCJ-02、转米制、出框断开、抽稀、短段丢弃。
     不串链：河网有交汇，串链会把两条河接成一条斜穿街区的假线；
     同色圆头画出来的 1~2 px 接缝，在十几像素宽的河面上看不出来。 */
  function packTileWaterways(tiles, z, lng, lat, half) {
    var segs = [], i;
    for (i = 0; i < tiles.length; i++) {
      try { readTileWaterways(tiles[i].data, z, tiles[i].x, tiles[i].y, segs); }
      catch (e) { /* 单张瓦片坏了不影响其它 */ }
    }
    var KX = 111319.49 * Math.cos(lat * Math.PI / 180), KY = 111319.49;
    var out = [];
    for (i = 0; i < segs.length; i++) {
      var it = segs[i], src = it.pts, run = [];
      for (var j = 0; j <= src.length; j++) {
        var q = j < src.length ? src[j] : null;
        var ok = false, mx = 0, my = 0;
        if (q && isFinite(q[0]) && isFinite(q[1])) {
          var gc = wgs2gcj(q[0], q[1]);
          mx = (gc[0] - lng) * KX;
          my = (gc[1] - lat) * KY;
          ok = Math.abs(mx) <= half && Math.abs(my) <= half;
        }
        if (ok) { run.push([mx, my]); continue; }
        if (run.length >= 2) {
          var simp = dpSimplify(run, 1.2), xy = [], L = 0;
          for (var k = 0; k < simp.length; k++) {
            xy.push(+simp[k][0].toFixed(1), +simp[k][1].toFixed(1));
            if (k) L += Math.hypot(simp[k][0] - simp[k - 1][0], simp[k][1] - simp[k - 1][1]);
          }
          if (L >= 12) {
            out.push({ xy: xy, w: it.w, measured: it.measured, tier: WATERWAY_TIER[it.cls] || 2,
                       cls: it.cls, name: it.name, L: Math.round(L) });
          }
        }
        run = [];
      }
    }
    if (window.__sbLog) window.__sbLog.push('waterway tiles ' + out.length + ' 段 / ' +
      Math.round(out.reduce(function (a, b) { return a + b.L; }, 0) / 1000) + ' km');
    return out;
  }

  function osmPolysTiles(lng, lat, half) {
    var Z = ROAD_TILE_Z, n = Math.pow(2, Z);
    var wg = gcj2wgs(lng, lat);
    var dLat = half / 111319.49;
    var dLng = half / (111319.49 * Math.cos(wg[1] * Math.PI / 180));
    function xOf(lo) { return (lo + 180) / 360 * n; }
    function yOf(la) {
      var r = la * Math.PI / 180;
      return (1 - Math.log(Math.tan(r) + 1 / Math.cos(r)) / Math.PI) / 2 * n;
    }
    var x0 = Math.floor(xOf(wg[0] - dLng)), x1 = Math.floor(xOf(wg[0] + dLng));
    var y0 = Math.floor(yOf(wg[1] + dLat)), y1 = Math.floor(yOf(wg[1] - dLat));
    var want = [];
    for (var x = x0; x <= x1; x++) for (var y = y0; y <= y1; y++) want.push({ x: x, y: y });
    return fetchTiles(want, Z, 30000).then(function (tiles) {
      if (!tiles.length) throw new Error('瓦片一张都没拿到');
      var res = packTilePolys(tiles, Z, lng, lat, half);
      /* 河道和面域用同一批瓦片，顺手取一次，不额外发请求 */
      try { res.ways = packTileWaterways(tiles, Z, lng, lat, half); }
      catch (e) { res.ways = []; }
      return res;
    });
  }

  function osmRoadsTiles(lng, lat, half) {
    var Z = ROAD_TILE_Z, n = Math.pow(2, Z);
    var wg = gcj2wgs(lng, lat);
    var dLat = half / 111319.49;
    var dLng = half / (111319.49 * Math.cos(wg[1] * Math.PI / 180));
    function xOf(lo) { return (lo + 180) / 360 * n; }
    function yOf(la) {
      var r = la * Math.PI / 180;
      return (1 - Math.log(Math.tan(r) + 1 / Math.cos(r)) / Math.PI) / 2 * n;
    }
    var x0 = Math.floor(xOf(wg[0] - dLng)), x1 = Math.floor(xOf(wg[0] + dLng));
    var y0 = Math.floor(yOf(wg[1] + dLat)), y1 = Math.floor(yOf(wg[1] - dLat));
    var want = [];
    for (var x = x0; x <= x1; x++) for (var y = y0; y <= y1; y++) want.push({ x: x, y: y });
    return fetchTiles(want, Z, 30000).then(function (tiles) {
      if (!tiles.length) throw new Error('瓦片一张都没拿到');
      return packTileRoads(tiles, Z, lng, lat, half);
    });
  }

  function osmRoads(lng, lat, half, timeoutMs) {
    /* 传进来的 lng/lat 是 GCJ-02（页面坐标）；Overpass 的 bbox 得用 WGS-84，
       否则查询框整体偏 300~500 m，2 km 视野会缺一边。 */
    var wg = gcj2wgs(lng, lat);
    var dLat = half / 111319.49;
    var dLng = half / (111319.49 * Math.cos(wg[1] * Math.PI / 180));
    var bbox = [wg[1] - dLat, wg[0] - dLng, wg[1] + dLat, wg[0] + dLng]
      .map(function (v) { return v.toFixed(5); }).join(',');
    var to = timeoutMs || 60000;
    var q = '[out:json][timeout:' + Math.floor(to / 1000) + '];' +
            'way["highway"~"^' + ROAD_CLS + '$"](' + bbox + ');out tags geom;';
    var KX = 111319.49 * Math.cos(lat * Math.PI / 180), KY = 111319.49;
    var cv = mkConv(lng, lat);
    var empty = { lines: [], names: [] };

    function pack(d) {
      var els = (d && d.elements) || [];
      var list = [];
      for (var i = 0; i < els.length; i++) {
        var e = els[i];
        if (e.type !== 'way' || !e.geometry || e.geometry.length < 2) continue;
        var tg = e.tags || {}, hw = tg.highway || '';
        if (!ROAD_HW.test(hw)) continue;
        /* 地下快速路（北横通道这类）和立交引道不进街道层 */
        if (ROAD_DROP.test(tg.name || '')) continue;
        if (tg.tunnel === 'yes' && (hw === 'trunk' || hw === 'motorway')) continue;
        var nm = (tg.name || '').replace(/\s+/g, '').trim();
        if (nm && (nm.length < 2 || /桥$/.test(nm) || isCrossName(nm))) nm = '';
        var pts = [];
        for (var j = 0; j < e.geometry.length; j++) {
          var g0 = e.geometry[j];
          if (g0 && isFinite(g0.lon) && isFinite(g0.lat)) pts.push([g0.lon, g0.lat]);
        }
        if (pts.length >= 2) {
          list.push({ pts: pts, w: ROAD_WID[hw] || 7.5,
                      tier: ROAD_TIER[hw] != null ? ROAD_TIER[hw] : 3, name: nm });
        }
      }
      var out = packRoadList(list, lng, lat, half, cv, KX, KY);
      if (window.__sbLog) window.__sbLog.push('osm roads ' + out.lines.length + ' 段 / ' +
        Math.round(out.lines.reduce(function (a, b) { return a + b.L; }, 0) / 1000) +
        ' km / names ' + out.names.length);
      return out;
    }

    /* 两台机器同时发，谁先回来用谁的。
       overpass-api.de 高峰期单条 4 km 见方的街道查询要 60~100 s、还经常 502；
       private.coffee 有时 10 s 就回。串行等一台会白等一个超时周期。 */
    var ROAD_HOSTS = ['https://overpass-api.de/api/interpreter',
                      'https://overpass.private.coffee/api/interpreter'];
    function one(host, ctrl) {
      var timer = setTimeout(function () { try { ctrl.abort(); } catch (e) {} }, to + 6000);
      return fetch(host + '?data=' + encodeURIComponent(q), {
        method: 'GET',          /* POST 会被网关 502 —— 和建筑、高速同一个坑 */
        cache: 'no-store',
        signal: ctrl.signal
      }).then(function (r) {
        clearTimeout(timer);
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
      }).then(function (d) {
        return pack(d);
      }).catch(function (err) {
        clearTimeout(timer);
        throw err;
      });
    }
    function attempt(n) {
      return new Promise(function (resolve) {
        var done = false, fails = 0, ctrls = [];
        ROAD_HOSTS.forEach(function (h, hi) {
          var ctrl = new AbortController();
          ctrls.push(ctrl);
          one(h, ctrl).then(function (res) {
            if (done) return;
            done = true;
            /* 赢家定了就把输家掐掉：Overpass 的并发配额按 IP 算，
               让输的那台继续跑完，会把后面的建筑/高速挤成 502。 */
            ctrls.forEach(function (c, ci) {
              if (ci !== hi) { try { c.abort(); } catch (e) {} }
            });
            resolve(res);
          }).catch(function (err) {
            if (err && err.name === 'AbortError') return;
            if (window.__sbLog) window.__sbLog.push('osm roads fail(' + (n + 1) + ') ' + h.split('/')[2] +
              ' ' + (err && err.message ? err.message : ''));
            if (++fails === ROAD_HOSTS.length && !done) { done = true; resolve(null); }
          });
        });
      }).then(function (res) {
        if (res) return res;
        if (n < 1) {
          return new Promise(function (r) { setTimeout(r, 3000); })
            .then(function () { return attempt(n + 1); });
        }
        return empty;
      });
    }
    return attempt(0);
  }

  /* Overpass 取 50 km 内的 motorway（trunk 在国内基本都是城市快速路，
     用户明确不要高架，所以只取 motorway）。 */
  function osmExpress(lng, lat, half, timeoutMs) {
    /* 传进来的是 GCJ-02，Overpass 的 bbox 要用 WGS-84（和 osmRoads 一致）。
       50 km 半径下 500 m 的偏移其实看不出，但换算一下不花钱。 */
    var wg = gcj2wgs(lng, lat);
    var dLat = half / 111319.49;
    var dLng = half / (111319.49 * Math.cos(wg[1] * Math.PI / 180));
    var bbox = [wg[1] - dLat, wg[0] - dLng, wg[1] + dLat, wg[0] + dLng]
      .map(function (v) { return v.toFixed(5); }).join(',');
    var to = timeoutMs || 150000;
    /* 必须 out body geom：只写 out geom 时不回 ways 的 nodes，
       packExpress 里「按首尾 node id 串链」整段失效，三千条 way 一条都串不起来。 */
    var trunkOn = !!(window.SITE_CONFIG && window.SITE_CONFIG.express &&
                     window.SITE_CONFIG.express.trunk);
    var sel = trunkOn ? '^(motorway|trunk)$' : '^motorway$';
    var q = '[out:json][timeout:' + Math.floor(to / 1000) + '];' +
            'way["highway"~"' + sel + '"](' + bbox + ');out body geom;';

    /* 并行请求两个 Overpass 实例，采用先返回的结果并取消另一路；均失败时尝试备用实例。 */
    var HOSTS = ['https://overpass-api.de/api/interpreter',
                 'https://overpass.private.coffee/api/interpreter',
                 'https://overpass.kumi.systems/api/interpreter'];
    function fire(host, ctrl) {
      var timer = setTimeout(function () { try { ctrl.abort(); } catch (e) {} }, to + 8000);
      return fetch(host + '?data=' + encodeURIComponent(q), {
        method: 'GET',          /* POST 会被网关 502，和街道/建筑同一个坑 */
        cache: 'no-store',
        signal: ctrl.signal
      }).then(function (r) {
        clearTimeout(timer);
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
      }).then(function (d) {
        var list = packExpress(d && d.elements, lng, lat, half, { trunk: trunkOn });
        if (window.__sbLog) window.__sbLog.push('osm express ' + list.length + ' 段 / ' +
          Math.round(list.reduce(function (a, b) { return a + b.L; }, 0) / 1000) +
          ' km @' + host.split('/')[2]);
        return list;
      }).catch(function (err) {
        clearTimeout(timer);
        if (err && err.name === 'AbortError') throw err;
        if (window.__sbLog) window.__sbLog.push('osm express fail ' + host.split('/')[2] +
          ' ' + (err && err.message ? err.message : ''));
        throw err;
      });
    }
    function race(hosts) {
      return new Promise(function (resolve) {
        var done = false, fails = 0, ctrls = [];
        hosts.forEach(function (h, hi) {
          var ctrl = new AbortController();
          ctrls.push(ctrl);
          fire(h, ctrl).then(function (list) {
            if (done) return;
            done = true;
            ctrls.forEach(function (c, ci) {
              if (ci !== hi) { try { c.abort(); } catch (e) {} }
            });
            resolve(list);
          }).catch(function () {
            if (done) return;
            if (++fails === hosts.length) { done = true; resolve(null); }
          });
        });
      });
    }
    return race(HOSTS.slice(0, 2)).then(function (r) {
      if (r) return r;
      /* 两台都没回再试第三台：宁可贵一点，也别让图层直接空掉。 */
      return race([HOSTS[2]]).then(function (r2) { return r2 || []; });
    });
  }

  /* 高德 v2.0 的 Geocoder 把结果整个包在 regeocode 里：
       { info:'OK', regeocode:{ formattedAddress, addressComponent:{...} } }
     直接读 r.formattedAddress / r.addressComponent 永远是 undefined，
     于是地址空白、adcode 拿不到、区县轮廓也就跟着整块消失。
     两种形态都兼容一下。 */
  function revGeo(lng, lat) {
    function attempt(n) {
      return pm(function (res) {
        var gc = new AMap.Geocoder({ radius: 1000, extensions: 'base' });
        gc.getAddress([lng, lat], function (st, r) {
          if (st !== 'complete' || !r) return res(null);
          var rg = r.regeocode || r;
          if (!rg || (!rg.formattedAddress && !rg.addressComponent)) return res(null);
          res(rg);
        });
      }, 12000).then(function (v) {
        if (v || n >= 1) return v;
        return new Promise(function (r) { setTimeout(r, 700); })
          .then(function () { return attempt(n + 1); });
      });
    }
    return attempt(0);
  }

  function districtPoly(adcode) {
    return pm(function (res) {
      var ds = new AMap.DistrictSearch({ level: 'district', subdistrict: 0, extensions: 'all' });
      ds.search(adcode, function (st, r) {
        if (st !== 'complete' || !r || !r.districtList || !r.districtList.length) return res(null);
        var d = r.districtList[0];
        if (!d.boundaries || !d.boundaries.length) return res(null);
        var rings = [];
        for (var b = 0; b < d.boundaries.length; b++) {
          var ring = d.boundaries[b].map(function (p) { return [+p.lng.toFixed(6), +p.lat.toFixed(6)]; });
          if (ring.length >= 3) rings.push(ring);
        }
        res(rings.length ? { name: d.name, adcode: d.adcode, rings: rings } : null);
      });
    }, 20000);
  }

  /* ---------------------------------------------------------------- build */
  /* 高德核心库不带 PlaceSearch / Geocoder / DistrictSearch，要按需加载插件 */
  function ensurePlugins() {
    return new Promise(function (res) {
      if (typeof AMap === 'undefined' || !AMap.plugin) return res();
      var done = false;
      function fin() { if (!done) { done = true; res(); } }
      setTimeout(fin, 9000);
      try {
        AMap.plugin(['AMap.PlaceSearch', 'AMap.Geocoder', 'AMap.DistrictSearch'], fin);
      } catch (e) { fin(); }
    });
  }

  async function build(c, onStep) {
    var log = onStep || function () {};
    var HALF = 2000;
    await ensurePlugins();
    var conv = mkConv(c.lng, c.lat);

    /* 互不依赖的请求一起发出去：街道矢量、用地瓦片、逆地理编码。
       串行跑的话光等网络就要十几秒。 */
    log('读取底图…');
    /* 首屏使用矢量瓦片，待 Overpass 返回后用带路名的中心线替换。查询范围覆盖视野对角线。 */
    var pRoadOsm = osmRoads(c.lng, c.lat, HALF + 1800, 120000).catch(function (err) {
      if (window.__sbLog) window.__sbLog.push('osm roads fail ' +
        (err && err.message ? err.message : ''));
      return null;
    });
    var pRoadTiles = osmRoadsTiles(c.lng, c.lat, HALF + 1800).catch(function (err) {
      if (window.__sbLog) window.__sbLog.push('road tiles fail ' +
        (err && err.message ? err.message : ''));
      return null;
    });
    /* 路网瓦片是兜底用的，只在真需要时抓（OSM 空手才走这一步） */
    var p8 = null;
    function grab8() { return p8 || (p8 = grab(8, 17, c.lng, c.lat, HALF, 2600)); }
    /* 栅格用地只在矢量面失败时才抓（72 张 z16 瓦片、约 2 s），所以做成惰性 */
    var p7 = null;
    function grab7() { return p7 || (p7 = grab(7, 17, c.lng, c.lat, HALF, 2600)); }
    var pGeo = revGeo(c.lng, c.lat);
    /* 高德 POI 接口同时发太多个会回 error，压到 2 个并发 */
    var lim = gate(2);
    /* 市内交通里的公交站：高德分类 150700 = 公交车站。
       1.8 km 内通常有几十上百个，翻 2 页取最近 50 个，够画了。 */
    var pBus = lim(function () { return poiSearchPaged('150700', c.lng, c.lat, 1800, 2); });
    /* 绿地/景点 POI（110100|110200|110000）不再取：绿地名称现在只来自「公园」
       这一类，那一批回来的是建筑、旧址、小广场的名字，图上不标了，白占一个请求。 */
    var pGreenPoi = Promise.resolve([]);
    var pRoadName = lim(function () { return poiSearch('190301', c.lng, c.lat, 900); });
    /* 高德 searchNearBy 的半径上限就是 50000 m，再大接口会直接回 error。 */
    /* 用高铁关键词检索客运站，限制站内设施和货运小站的干扰。 */
    var pRail = lim(function () {
      return poiSearchPaged('150200', c.lng, c.lat, 50000, 2, '高铁');
    });
    var pAir = lim(function () { return poiSearchPaged('150100', c.lng, c.lat, 50000, 2, '机场'); });
    /* 检索地铁站本体，避免出入口占满当前页。 */
    /* 一页只有 25 条，3 km 半径的市区地铁站不止这个数，而页面上要求
       "点有几个就写几个名字"，所以翻两页（50 条）再按本体去重。 */
    var pMetro = lim(function () { return poiSearchPaged('150500', c.lng, c.lat, 3000, 2, '地铁站'); });
    /* 商业、行政、体育和公园分别请求，保证每类都有独立的结果配额。 */
    /* 细分类别分别翻页；商业使用购物中心与普通商场的类别码。 */
    var pShop = lim(function () { return poiSearchPaged('060101|060102', c.lng, c.lat, HALF, 2); });
    var pGov = lim(function () {
      return poiSearchPaged('130101|130102|130103|130104', c.lng, c.lat, HALF, 3);
    });
    var pSport = lim(function () { return poiSearchPaged('080100', c.lng, c.lat, HALF, 2); });
    var pPark = lim(function () { return poiSearchPaged('110101', c.lng, c.lat, HALF, 2); });
    /* 城市名（地市级地名 POI，如"温州市"） */
    var pCityNm = lim(function () { return poiSearch('190104', c.lng, c.lat, 60000); });
    /* 河湖名：自然地名里的 河流 / 湖泊 / 水库 */
    var pWaterNm = lim(function () { return poiSearch('190200', c.lng, c.lat, 9000); });
    /* 高速名：从收费站 / 高速入口的名字里抠（高德没有单独的"某某高速"POI） */
    /* 4 页：50 km 内的收费站/服务区很多，2 页经常只抠出 3 个路名 */
    var pExpNm = lim(function () { return poiSearchPaged('180000', c.lng, c.lat, 50000, 4); });
    var pPoi = Promise.all([pBus, pGreenPoi, pRoadName, pRail, pAir, pMetro,
                            pShop, pGov, pSport, pPark, pCityNm, pWaterNm, pExpNm]);

    var geo = await pGeo;
    var district = null, cityInfo = { name: '', addr: '' };
    var pDist = null;
    if (geo) {
      cityInfo.addr = geo.formattedAddress || '';
      var ac = geo.addressComponent || {};
      cityInfo.name = (ac.district || ac.city || '') + '';
      /* 直辖市的 city 可能为空，此时使用 province。 */
      cityInfo.city = ac.city || ac.province || '';
      if (ac.adcode) pDist = districtPoly(ac.adcode);
    }

    /* ---------- 道路 ----------
       OSM 街道中心线是首选：整条街一条 way，不断、不错位、名字也就地可取。
       瓦片骨架只在 OSM 空手（断网 / Overpass 限流）或明显缺数据时才用。
       判据用「段数 ≥ 60 且总长 ≥ 15 km」—— 2 km 见方的城区随便都不止这个数，
       而一片农田/山区 OSM 可能只有几条主路，那时瓦片也给不出什么，直接用它。

       只等 12 s：瓦片正常 3~5 s 就回，12 s 够 TileJSON + 9 张并发；
       真没回就先上瓦片骨架，把 pRoad 挂到结果里当"升级包"，
       页面在第一帧画完之后收到就整体替换（和建筑、高速一个套路）。 */
    log('读取街道…');
    var roadLines = [], roadNames = [], roadPending = null;
    /* 谁先给出可用数据就用谁：OSM 正常时优先（v12 主源），瓦片快时先顶上。
       14 s 是给瓦片的上限（正常 4~6 s，TileJSON 抖动也能兜住）。 */
    var firstRoad = await Promise.race([
      pRoadOsm.then(function (r) {
        return (r && r.lines && r.lines.length >= 60) ? { src: 'osm', r: r }
                                                      : new Promise(function () {});
      }),
      pRoadTiles.then(function (r) {
        return (r && r.lines && r.lines.length >= 200) ? { src: 'tiles', r: r }
                                                       : new Promise(function () {});
      }),
      new Promise(function (res) {
        setTimeout(function () { res(null); }, 14000);
      })
    ]);
    /* 两条都没赶上（网络慢/限流）：再给 OSM 25 s，仍旧空手才退到栅格骨架 */
    if (!firstRoad) {
      var lateOsm = await Promise.race([pRoadOsm, new Promise(function (res) {
        setTimeout(function () { res(null); }, 25000);
      })]);
      if (lateOsm && lateOsm.lines && lateOsm.lines.length >= 60) {
        firstRoad = { src: 'osm', r: lateOsm };
      }
    }
    var osmR = firstRoad && firstRoad.src === 'osm' ? firstRoad.r : null;
    var tileR = firstRoad && firstRoad.src === 'tiles' ? firstRoad.r : null;
    var osmLen = 0;
    if (osmR && osmR.lines && osmR.lines.length) {
      for (var ol = 0; ol < osmR.lines.length; ol++) osmLen += osmR.lines[ol].L || 0;
    }
    var useOsm = !!(osmR && osmR.lines && osmR.lines.length >= 60 && osmLen >= 15000);
    /* 高速：OpenFreeMap 矢量瓦片（几秒）+ Overpass 兜底。
       两条路都是按当前坐标现取，没有预置、没有缓存；
       瓦片走 CDN，和街道那单的 Overpass 查询互不干扰，所以不用再排队等。 */
    var pExp = osmExpressTiles(c.lng, c.lat, 50000).catch(function (err) {
      if (window.__sbLog) window.__sbLog.push('express tiles fail ' +
        (err && err.message ? err.message : '') + ' → 退回 Overpass');
      return osmExpress(c.lng, c.lat, 50000, 200000);
    });
    var g8 = null;
    if (useOsm) {
      roadLines = osmR.lines;
      roadNames = osmR.names || [];
      if (window.__sbLog) window.__sbLog.push('道路源 = OSM（v12 主源）' +
        roadLines.length + ' 段 / ' + Math.round(osmLen / 1000) + ' km');
    } else if (tileR) {
      roadLines = tileR.lines;
      roadNames = tileR.names || [];
      /* OSM 优先：瓦片先顶上，OSM 到了再整体替换。
         只有总长不低于瓦片版 60% 才换 —— Overpass 限流时会回半截数据，
         拿它盖掉完整的一版反而更差。 */
      var tileLen = 0;
      for (var tl = 0; tl < tileR.lines.length; tl++) tileLen += tileR.lines[tl].L || 0;
      roadPending = pRoadOsm.then(function (r) {
        if (!r || !r.lines || r.lines.length < 60) return null;
        var ol2 = 0;
        for (var k = 0; k < r.lines.length; k++) ol2 += r.lines[k].L || 0;
        if (ol2 < tileLen * 0.6) {
          if (window.__sbLog) window.__sbLog.push('osm roads 偏少（' +
            Math.round(ol2 / 1000) + ' km < 瓦片 ' + Math.round(tileLen / 1000) +
            ' km）→ 保留瓦片');
          return null;
        }
        if (window.__sbLog) window.__sbLog.push('道路升级为 OSM（v12 主源）' +
          r.lines.length + ' 段 / ' + Math.round(ol2 / 1000) + ' km');
        return r;
      });
    } else {
      roadPending = pRoadOsm;
      log('识别道路…');
      g8 = await grab8();
      roadLines = roads(g8, conv);
    }

    /* 水面 / 绿地 / 河道：优先级 ① OpenFreeMap 矢量（同一批瓦片：
       water 面 + waterway 线）→ ② 高德 z16 栅格色带。
       ① 是真正的多边形（边界 0.6 m），② 是从 9.5 m 一格的栅格色带抠出来的，
       边缘是像素锯齿 —— 后者就是"水面和绿地碎成一堆小块"的原因。
       ① 只要拿到了、面数正常，就完全不碰 ②（连 72 张栅格都不抓）。
       注意：城区河道在 OSM 里是 waterway 线、没有面，只判 water 面数会误判
       "矢量数据完整"，所以河道单独取。 */
    log('识别用地…');
    var greens = [], waters = [], waterWays = [], polyR = null;
    try {
      polyR = await Promise.race([osmPolysTiles(c.lng, c.lat, HALF + 1800),
        new Promise(function (r) { setTimeout(function () { r(null); }, 14000); })]);
    } catch (e) { polyR = null; }
    if (polyR && (polyR.water.length + polyR.green.length) >= 4) {
      greens = polyR.green; waters = polyR.water;
      waterWays = polyR.ways || [];
      if (window.__sbLog) window.__sbLog.push('用地源 = 矢量瓦片（水 ' + waters.length +
        ' 面 / 河道 ' + waterWays.length + ' 段 / 绿 ' + greens.length + '）');
    } else {
      if (window.__sbLog) window.__sbLog.push('用地源 = 高德栅格（矢量失败或面数太少）');
      var g7 = await grab7();
      greens = polys(g7, false, conv, 900);
      waters = polys(g7, true, conv, 500);
    }

    /* 高速不再有"栅格骨架占位"：z11 一格 4 px，50 km 尺度上立交/跨江/隧道口全断，
       画出来全是断头和假支路（用户原话：「大量断头路」）。宁可空几秒，
       等矢量中心线到了直接画，也不再先摆一版错的。 */
    var exp = [];

    /* 区县边界必须在等 POI 之前收掉。原来放在最后，pm 的 10 s 超时经常
       在等 POI 翻页的时候就到了 —— 请求其实成功了也被判失败，
       结果「所在区县」有名字、没轮廓。 */
    if (pDist) district = await pDist;

    log('检索周边设施…');
    var poi = await pPoi;
    var busRaw = poi[0] || [], roadRaw = poi[2] || [];
    /* 优先取 OSM 路名，缺失时从道路 POI 中筛除路口并合并重复路段。 */
    if (!roadNames.length) {
      var seenRn = {};
      for (var rn0 = 0; rn0 < roadRaw.length; rn0++) {
        var rnName = normName(roadRaw[rn0].name);
        if (!rnName || seenRn[rnName] || isCrossName(rnName) || /桥$/.test(rnName)) continue;
        seenRn[rnName] = 1;
        roadNames.push({ name: rnName, lng: roadRaw[rn0].lng, lat: roadRaw[rn0].lat });
      }
      roadNames = roadNames.slice(0, 16);
    }
    var railRaw = poi[3] || [], airRaw = poi[4] || [], metroRaw = poi[5] || [];
    var cityRaw = poi[10] || [], waterRaw = poi[11] || [], expRaw = poi[12] || [];


    /* 公交站：只要「公交车站」本体，剔掉站台/出入口这类子设施，
       同名去重（一个站台双向各挂一个 POI），按距离取最近的 50 个 */
    var bus = [], seenB = {};
    for (var bi = 0; bi < busRaw.length; bi++) {
      var bb = busRaw[bi];
      /* 高德公交站的 type 是「交通设施服务;公交车站;公交车站相关」，
         第三段是"公交车站相关"而不是"公交车站"，用 subType 精确比对会全部落空。 */
      if ((bb.type || '').indexOf('公交车站') < 0) continue;
      var bn = normName(bb.name);
      if (!bn || seenB[bn]) continue;
      seenB[bn] = 1;
      bus.push({ name: bn, lng: bb.lng, lat: bb.lat, dist: bb.dist });
    }
    bus.sort(function (a, b) { return a.dist - b.dist; });
    bus = bus.slice(0, 50);

    /* ---------- 配套：商业 / 行政 / 体育 / 公园 ---------- */
    /* 只收「片区级」的设施。分析图要回答的是"这一带有什么"，不是"楼下有什么"：
       路边便利店、社区居委会、健身房、口袋公园画上去只会把图糊掉。
       高德 type 的第三段（subType）自带层级和类型，比按名字猜可靠得多：
         · 商场   购物中心 / 普通商场 / 商场（060100 本来就只有商场，没有便利店）
         · 机关   国家级 / 省直辖市级 / 地市级 / 区县级
                  —— 乡镇级、乡镇以下级就是街道办和居委会，全丢
         · 场馆   综合体育馆 / 体育场 / 体育馆 / 游泳馆
                  —— 健身中心、瑜伽、台球厅、各类球场全丢
       公园没有层级字段，改用绿地面域的实际面积（greens 每项都带 area）。 */
    var AMEN_KEEP = {
      shop: ['购物中心', '普通商场', '商场'],
      /* 乡镇级要放进来 —— 街道办事处就在这一级（"与市民生活相关"） */
      gov: ['国家级机关及事业单位', '省直辖市级政府及事业单位',
            '地市级政府及事业单位', '区县级政府及事业单位',
            '乡镇级政府及事业单位'],
      sport: ['综合体育馆', '综合体育场馆', '体育场', '体育馆',
              '田径场', '游泳馆', '体育中心']
    };
    /* 按机构子类型及名称筛选面向市民的办事场所，排除下属执法、物资和行业监管机构。 */
    var GOV_CORE = /人民政府|市民中心|政务服务中心|便民服务中心|街道办事处|出入境|派出所|公安[局署]|公安分局|人民法院|人民检察院|税务(局|分局)|社保|医保|公积金|不动产登记|婚姻登记|人力资源和社会保障|财政|规划和自然资源|住房和城乡建设|市场监督管理|管理委员会|管委会/;
    /* 注意不能排"中心"和"办事处" —— 市民中心、政务服务中心、街道办都会被误伤 */
    var GOV_DROP = /居委会|居民委员会|社区居委|驻沪|驻京|驻上海|外地政府|调解|来访|窗口|总支|支部|监察|检查队|执法队|物资库|抢修|征收|拆迁|关心下一代|退休|老龄|协会|学会|基金会|管理所|管理处|监测|环卫|市容|绿化|废弃物/;
    var GOV_LV = { '国家级机关及事业单位': 0, '省直辖市级政府及事业单位': 1,
                   '地市级政府及事业单位': 2, '区县级政府及事业单位': 3 };
    /* 仅去除方位或分座后缀，以商场主体名称合并分座。 */
    var WING = /(?:[东西南北中][座馆区楼]|[AB][座楼]|第[一二三四五六七八九十\d]+街)$/;
    /* 体育只要大型场馆：体育馆 / 体育场 / 游泳馆 / 体育中心 / 文体中心 /
       活动中心 / 全民健身中心。挂着"游泳馆"名头的商业健身房、俱乐部、
       各类球场一律不要。 */
    var SPORT_CORE = /体育馆|体育场|游泳馆|游泳中心|体育中心|文体中心|活动中心|全民健身中心|体育公园/;
    var SPORT_DROP = /健身|瑜伽|台球|培训|射箭|跆拳道|武术|体适能|俱乐部|工作室|私教|普拉提|球馆|球场/;

    function ringHas(rg, lng, lat) {
      var inside = false;
      for (var i = 0, j = rg.length - 1; i < rg.length; j = i++) {
        var xi = rg[i][0], yi = rg[i][1], xj = rg[j][0], yj = rg[j][1];
        if ((yi > lat) !== (yj > lat) &&
            lng < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
      }
      return inside;
    }
    /* 点到线段的距离（先用度数换算成米，再算，避免经度方向失真） */
    function segDistM(px, py, ax, ay, bx, by) {
      var dx = bx - ax, dy = by - ay, dd = dx * dx + dy * dy, t = 0;
      if (dd > 0) t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / dd));
      return Math.hypot(px - (ax + t * dx), py - (ay + t * dy));
    }
    /* 以包含公园点位的最大绿地面积估算公园规模。 */
    function parkArea(lng, lat) {
      var i, best = 0;
      for (i = 0; i < greens.length; i++) {
        var a0 = greens[i].area || 0;
        if (a0 > best && ringHas(greens[i].ring, lng, lat)) best = a0;
      }
      if (best) return best;
      /* 入口可能位于绿地外。取边界距点位不超过 200 m 的最大绿地面积。 */
      var kx = 111319.49 * Math.cos(lat * Math.PI / 180), ky = 111319.49;
      var px = lng * kx, py = lat * ky, big = 0;
      for (i = 0; i < greens.length; i++) {
        var rg = greens[i].ring, a1 = greens[i].area || 0;
        if (a1 <= big) continue;
        var mind = 1e9;
        for (var j = 0; j < rg.length; j++) {
          var p1 = rg[j], p2 = rg[(j + 1) % rg.length];
          var d = segDistM(px, py, p1[0] * kx, p1[1] * ky, p2[0] * kx, p2[1] * ky);
          if (d < mind) mind = d;
          if (mind <= 0) break;
        }
        if (mind <= 200) big = a1;
      }
      return big;
    }

    var amen = { shop: [], gov: [], sport: [], park: [] };
    var AMEN_SRC = { shop: poi[6], gov: poi[7], sport: poi[8], park: poi[9] };
    var AMEN_RANK = { '购物中心': 0, '普通商场': 1, '商场': 2 };

    for (var bk in AMEN_SRC) {
      var list = AMEN_SRC[bk] || [], keep = AMEN_KEEP[bk], picked = [];
      for (var ai = 0; ai < list.length; ai++) {
        var am = list[ai], st = subType(am.type);
        if (bk === 'park' && (am.type || '').indexOf('动物园') >= 0) continue;
        if (keep) {
          var hit = false;
          for (var ni = 0; ni < keep.length; ni++) if (st === keep[ni]) hit = true;
          if (!hit) continue;
        }
        if (bk === 'gov' && GOV_DROP.test(am.name || '')) continue;
        if (bk === 'sport' &&
            (SPORT_DROP.test(am.name || '') || !SPORT_CORE.test(am.name || ''))) continue;
        var anm = normName(am.name);
        if (!anm) continue;
        picked.push({ name: anm, lng: am.lng, lat: am.lat, dist: am.dist || 0, st: st });
      }

      if (bk === 'park') {
        for (var pi = 0; pi < picked.length; pi++) {
          picked[pi].area = parkArea(picked[pi].lng, picked[pi].lat);
        }
        /* 按绿地面积降序取前六个。瓦片中的建筑和水面可能分割公园，因此不设固定面积下限。 */
        picked = picked.filter(function (t) {
          return !/口袋|街心|小游园|微型|社区公园/.test(t.name);
        });
        picked.sort(function (a, b) { return b.area - a.area || a.dist - b.dist; });
        amen[bk] = picked.slice(0, 6);
        continue;
      }

      if (bk === 'gov') {
        /* 国家级（部委）照单全收；省市级以下按机构类型筛：
           财政/规划/教育/公安/法院/检察院/管委会这类才是分析图上要的，
           邮政管理局、海事局、烟酒专卖局这类垂直行业管理局一律不要。 */
        /* 国家级不再一律放行 —— "国家能源局华东监管局"这种行业监管机构
           对市民生活没有意义，同样要走名称判据。 */
        var core = picked.filter(function (t) {
          return !GOV_DROP.test(t.name) && GOV_CORE.test(t.name);
        });
        picked = core;
      }
      /* 商场分座先归一为基名，再按名称去重。 */
      var out = [], names = [];
      for (var qi = 0; qi < picked.length; qi++) {
        var t = picked[qi], dup = false;
        var base = t.name.replace(WING, '');
        var key = (bk === 'shop' && base.length >= 3) ? base : t.name;
        /* 统一管理委员会简称和办公室后缀后，合并重复机构。 */
        if (bk === 'gov') key = key.replace(/管理委员会/g, '管委会').replace(/(办公室|办事处)$/, '');
        for (var nk = 0; nk < names.length; nk++) {
          if (names[nk] === key) { dup = true; break; }
          if (bk === 'gov' && names[nk].length >= 4 && key.length >= 4 &&
              (names[nk].indexOf(key) >= 0 || key.indexOf(names[nk]) >= 0)) {
            dup = true; break;
          }
        }
        if (dup) continue;
        names.push(key);
        out.push(t);
      }
      /* 商业：先按档次（购物中心 > 普通商场 > 商场）再按距离 */
      if (bk === 'shop') {
        out.sort(function (a, b) {
          return (AMEN_RANK[a.st] || 0) - (AMEN_RANK[b.st] || 0) || a.dist - b.dist;
        });
      } else {
        out.sort(function (a, b) { return a.dist - b.dist; });
      }
      amen[bk] = out.slice(0, 8);
    }

    /* 城市名取自逆地理结果，地名 POI 仅提供标注坐标。 */
    var cityName = null;
    if (cityInfo.city) {
      var hit = null;
      for (var ci = 0; ci < cityRaw.length; ci++) {
        if (cityRaw[ci].name === cityInfo.city) { hit = cityRaw[ci]; break; }
      }
      cityName = { name: cityInfo.city,
                   lng: hit ? hit.lng : c.lng,
                   lat: hit ? hit.lat : c.lat };
      if (!hit) cityName.far = true;   /* 没有坐标就贴在场地附近，不和区县名打架 */
    }

    /* ---------- 河湖名 ---------- */
    var waterNames = [], seenW = {};
    for (var wi = 0; wi < waterRaw.length; wi++) {
      var wv = waterRaw[wi], wt = wv.type || '';
      if (wt.indexOf('自然地名') < 0) continue;
      var wn0 = wv.name || '';
      if (!/河流|湖泊|水库|运河/.test(wt) && !/[江河湖浦]$/.test(wn0)) continue;
      if (seenW[wn0]) continue;
      seenW[wn0] = 1;
      waterNames.push({ name: wn0, lng: wv.lng, lat: wv.lat });
      if (waterNames.length >= 10) break;
    }

    /* 读取 waterway 的 name 字段，并将离基地最近的顶点作为河名标注位置。 */
    if (waterWays.length) {
      var KXW = 111319.49 * Math.cos(c.lat * Math.PI / 180), KYW = 111319.49;
      /* 同名河道可跨多个瓦片。比较全部同名线段的顶点，使标注落在最近处。 */
      var wayBest = {}, wayOrder = [];
      for (var wj2 = 0; wj2 < waterWays.length; wj2++) {
        var ww2 = waterWays[wj2];
        if (!ww2.name) continue;
        var rec = wayBest[ww2.name];
        if (!rec) {
          rec = wayBest[ww2.name] = { d2: 1e18, x: 0, y: 0 };
          wayOrder.push(ww2.name);
        }
        for (var vi2 = 0; vi2 + 1 < ww2.xy.length; vi2 += 2) {
          var d2 = ww2.xy[vi2] * ww2.xy[vi2] + ww2.xy[vi2 + 1] * ww2.xy[vi2 + 1];
          if (d2 < rec.d2) { rec.d2 = d2; rec.x = ww2.xy[vi2]; rec.y = ww2.xy[vi2 + 1]; }
        }
      }
      for (var wi2 = 0; wi2 < wayOrder.length && waterNames.length < 14; wi2++) {
        var nm2 = wayOrder[wi2];
        if (seenW[nm2]) continue;                 /* 高德 POI 已经标过同名水体 */
        var rr2 = wayBest[nm2];
        if (!rr2 || Math.sqrt(rr2.d2) > 2000) continue;   /* 2 km 外的名字留给更远的视野 */
        seenW[nm2] = 1;
        waterNames.push({ name: nm2, kind: 'way',
          lng: c.lng + rr2.x / KXW, lat: c.lat + rr2.y / KYW });
      }
    }

    /* ---------- 高速名 ----------
       高德没有"沈海高速"这样的独立 POI，但收费站/入口的名字里一定带着，
       形如「温州西收费站(G1513温丽高速出口)」「S26诸永高速入口(东北向)」，
       抠出前半段就是标准写法。标签落在收费站/入口位置，正好沿线路分布。 */
    var EXP_RE = /([GS]?\d{0,4}[\u4e00-\u9fa5]{2,8}?高速)/;
    var expLabels = [], seenE = {};
    for (var ei2 = 0; ei2 < expRaw.length; ei2++) {
      var m0 = EXP_RE.exec(expRaw[ei2].name || '');
      if (!m0) continue;
      if (seenE[m0[1]]) continue;
      seenE[m0[1]] = 1;
      expLabels.push({ name: m0[1], lng: expRaw[ei2].lng, lat: expRaw[ei2].lat });
      if (expLabels.length >= 16) break;
    }
    /* 名字落到线上。收费站/入口的坐标经常在 30~40 km 外，5 km 视野里
       根本进不了屏 —— 把每个名字吸附到最近的高速线顶点，才能沿线路标注。 */
    for (var ek = 0; ek < expLabels.length; ek++) {
      var lb = expLabels[ek], bestD = 1e18, bestP = null;
      for (var ee = 0; ee < exp.length; ee++) {
        var ep = exp[ee].path;
        for (var pp = 0; pp < ep.length; pp++) {
          var ddx = ep[pp][0] - lb.lng, ddy = ep[pp][1] - lb.lat;
          var d2 = ddx * ddx + ddy * ddy;
          if (d2 < bestD) { bestD = d2; bestP = ep[pp]; }
        }
      }
      if (bestP) { lb.lng = bestP[0]; lb.lat = bestP[1]; }
    }

    /* 根据设施类型的第三段筛选铁路客运站，排除候车室、站台和检票口。 */
    var rail = [], seen = {};
    for (var i = 0; i < railRaw.length; i++) {
      var rr0 = railRaw[i];
      if (subType(rr0.type) !== '火车站') continue;
      var nm = cutAt(normName(rr0.name), '站');
      if (!nm || nm.length < 2) continue;
      if (seen[nm]) continue;
      seen[nm] = 1;
      rail.push({ name: nm, lng: rr0.lng, lat: rr0.lat, dist: rr0.dist });
    }
    /* 50 km 半径内大城市的车站本体能有二三十个，截太多会漏掉环线站 */
    rail.sort(function (a, b) { return a.dist - b.dist; });
    rail = rail.slice(0, 40);
    /* 最近的 3 个按主站画大字，其余小字 */
    for (var rl2 = 0; rl2 < rail.length; rl2++) rail[rl2].lv = rl2 < 3 ? 'main' : 'sub';

    /* 机场：同一座机场有 T1/T2/T3 好几个 POI，名字互相是前缀，取最短的那个 */
    var airCand = [];
    for (var j = 0; j < airRaw.length; j++) {
      var aa = airRaw[j];
      if (subType(aa.type) !== '飞机场') continue;
      var rawAir = normName(aa.name);
      if (rawAir.indexOf('机场') < 0) continue;
      var an = cutAt(rawAir, '机场');
      if (!an) continue;
      airCand.push({ name: an, lng: aa.lng, lat: aa.lat, dist: aa.dist });
    }
    airCand.sort(function (a, b) { return a.name.length - b.name.length || a.dist - b.dist; });
    var air = [];
    for (var k = 0; k < airCand.length; k++) {
      var dup = false;
      for (var m0 = 0; m0 < air.length; m0++) {
        if (airCand[k].name.indexOf(air[m0].name) === 0) { dup = true; break; }
      }
      if (!dup) air.push(airCand[k]);
    }
    air = air.slice(0, 6);

    /* 地铁站：只要「地铁站」本体，剔掉 A/B/C 出入口 */
    var metro = [], seenM = {};
    for (var q0 = 0; q0 < metroRaw.length; q0++) {
      var mm0 = metroRaw[q0];
      if (subType(mm0.type) !== '地铁站') continue;
      var mn = normName(mm0.name).replace(/地铁站$/, '');
      if (!mn || seenM[mn]) continue;
      seenM[mn] = 1;
      metro.push({ name: mn, lng: mm0.lng, lat: mm0.lat, dist: mm0.dist });
    }
    metro.sort(function (a, b) { return a.dist - b.dist; });
    /* 为视野边缘的车站预留候选名额。 */
    metro = metro.slice(0, 20);

    return {
      /* g7/g8 现在都是惰性的：街道和用地都走矢量瓦片时两张栅格都不会抓，
         这里必须给兜底，否则 (g8||g7).mpp 直接抛异常，整个构建失败。 */
      meta: { center: [c.lng, c.lat], half: HALF,
              mpp: (g8 || g7) ? (g8 || g7).mpp : 0,
              name: c.name || '', addr: cityInfo.addr, district: cityInfo.name },
      /* 街道升级包：只有先用瓦片骨架顶班时才有，OSM 到了由页面整体替换 */
      R: roadPending,
      /* 高速升级包：build 一开始就在查，OSM 中心线到了由页面整体替换 */
      E: pExp,
      L: {
        green: greens, water: waters, waterWays: waterWays,
        roadLines: roadLines,
        bus: bus,
        amenities: amen,
        waterNames: waterNames,
        greenPois: [],            /* 已停用：绿地名称只来自公园 */
        roadNames: roadNames
      },
      C: {
        district: district || { name: cityInfo.name, rings: [] },
        express: exp, expressLabels: expLabels, rail: rail, air: air, metro: metro,
        cityName: cityName
      }
    };
  }

  window.SiteBuilder = window.SiteBuilder || {};
  window.SiteBuilder.build = build;
  window.SiteBuilder.pxToLat = pxToLat;
  window.SiteBuilder.pxToLng = pxToLng;
  window.SiteBuilder.osmExpress = osmExpress;
  window.SiteBuilder.osmExpressTiles = osmExpressTiles;
  window.SiteBuilder.osmRoads = osmRoads;
  window.SiteBuilder.osmRoadsTiles = osmRoadsTiles;
  window.SiteBuilder.osmPolysTiles = osmPolysTiles;
  window.SiteBuilder.gcj2wgs = gcj2wgs;
})();
