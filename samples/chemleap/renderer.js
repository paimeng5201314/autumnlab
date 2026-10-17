/* ChemLeap — original local renderer. No network, dependencies or animation loop. */
(function () {
  'use strict';

  const TAU = Math.PI * 2;
  const clamp = (value, low, high) => Math.min(high, Math.max(low, Number(value) || 0));
  const mix = (a, b, amount) => a + (b - a) * amount;
  const add = (a, b) => a.map((value, index) => value + b[index]);
  const sub = (a, b) => a.map((value, index) => value - b[index]);
  const dot = (a, b) => a.reduce((total, value, index) => total + value * b[index], 0);
  const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  function normal(vector) {
    const length = Math.hypot(...vector) || 1;
    return vector.map(value => value / length);
  }
  function color(value, fallback = '#8ab6a6') {
    if (!/^#[a-f\d]{6}$/i.test(value || '')) value = fallback;
    return [1, 3, 5].map(index => parseInt(value.slice(index, index + 2), 16) / 255);
  }
  const palette = {
    jade: color('#75a895'), dark: color('#3a6b5b'), cream: color('#fff1d6'), orange: color('#e99a60'),
    floor: color('#e5e7da'), white: color('#fffaf0'), line: color('#a3b7a3'), shadow: color('#cad3c5')
  };
  function multiply(a, b) {
    const result = new Float32Array(16);
    for (let column = 0; column < 4; column++) {
      for (let row = 0; row < 4; row++) {
        result[column * 4 + row] = a[row] * b[column * 4] + a[4 + row] * b[column * 4 + 1] +
          a[8 + row] * b[column * 4 + 2] + a[12 + row] * b[column * 4 + 3];
      }
    }
    return result;
  }
  function perspective(aspect, distance) {
    const f = 1 / Math.tan(39 * Math.PI / 360), near = 0.1, far = distance + 90;
    return new Float32Array([f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1,
      0, 0, 2 * far * near / (near - far), 0]);
  }
  function lookAt(eye, target) {
    const z = normal(sub(eye, target)), x = normal(cross([0, 1, 0], z)), y = cross(z, x);
    return new Float32Array([x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0,
      -dot(x, eye), -dot(y, eye), -dot(z, eye), 1]);
  }
  function transform(position, scale, angle = 0) {
    const cosine = Math.cos(angle), sine = Math.sin(angle);
    return new Float32Array([cosine * scale[0], 0, -sine * scale[0], 0, 0, scale[1], 0, 0,
      sine * scale[2], 0, cosine * scale[2], 0, ...position, 1]);
  }
  function normalTransform(scale, angle = 0) {
    const cosine = Math.cos(angle), sine = Math.sin(angle);
    return new Float32Array([cosine / scale[0], 0, -sine / scale[0], 0, 1 / scale[1], 0,
      sine / scale[2], 0, cosine / scale[2]]);
  }
  function triangle(data, a, b, c) {
    const n = normal(cross(sub(b, a), sub(c, a)));
    for (const point of [a, b, c]) data.push(...point, ...n);
  }
  function boxMesh() {
    const data = [];
    const faces = [
      [[-1, 1, -1], [-1, 1, 1], [1, 1, 1], [1, 1, -1]],
      [[-1, -1, 1], [-1, -1, -1], [1, -1, -1], [1, -1, 1]],
      [[-1, -1, 1], [1, -1, 1], [1, 1, 1], [-1, 1, 1]],
      [[1, -1, -1], [-1, -1, -1], [-1, 1, -1], [1, 1, -1]],
      [[1, -1, 1], [1, -1, -1], [1, 1, -1], [1, 1, 1]],
      [[-1, -1, -1], [-1, -1, 1], [-1, 1, 1], [-1, 1, -1]]
    ];
    for (const [a, b, c, d] of faces) {
      triangle(data, a.map(v => v * 0.5), b.map(v => v * 0.5), c.map(v => v * 0.5));
      triangle(data, a.map(v => v * 0.5), c.map(v => v * 0.5), d.map(v => v * 0.5));
    }
    return new Float32Array(data);
  }
  function sphereMesh() {
    const data = [], latitude = 8, longitude = 12;
    function point(row, column) {
      const phi = row / latitude * Math.PI, theta = column / longitude * TAU;
      return [Math.sin(phi) * Math.cos(theta), Math.cos(phi), Math.sin(phi) * Math.sin(theta)];
    }
    for (let row = 0; row < latitude; row++) {
      for (let column = 0; column < longitude; column++) {
        const a = point(row, column), b = point(row + 1, column), c = point(row + 1, column + 1), d = point(row, column + 1);
        if (row !== latitude - 1) triangle(data, a, c, b);
        if (row !== 0) triangle(data, a, d, c);
      }
    }
    return new Float32Array(data);
  }
  function discMesh() {
    const data = [];
    for (let index = 0; index < 36; index++) {
      const a = index / 36 * TAU, b = (index + 1) / 36 * TAU;
      triangle(data, [0, 0, 0], [Math.cos(b), 0, Math.sin(b)], [Math.cos(a), 0, Math.sin(a)]);
    }
    return new Float32Array(data);
  }
  const geometry = { box: boxMesh(), sphere: sphereMesh(), disc: discMesh() };

  const vertexSource = `
    attribute vec3 aPosition;
    attribute vec3 aNormal;
    uniform mat4 uViewProjection;
    uniform mat4 uModel;
    uniform mat3 uNormal;
    varying vec3 vNormal;
    varying vec3 vWorld;
    void main() {
      vec4 world = uModel * vec4(aPosition, 1.0);
      vWorld = world.xyz;
      vNormal = normalize(uNormal * aNormal);
      gl_Position = uViewProjection * world;
    }`;
  const fragmentSource = `
    precision mediump float;
    uniform vec3 uColor;
    uniform float uUnlit;
    varying vec3 vNormal;
    varying vec3 vWorld;
    void main() {
      vec3 n = normalize(vNormal);
      float light = max(dot(n, normalize(vec3(-0.45, 0.85, 0.55))), 0.0);
      float hemisphere = 0.1 * (n.y * 0.5 + 0.5);
      vec3 shaded = uColor * (0.63 + light * 0.34 + hemisphere);
      shaded = mix(shaded, uColor, uUnlit);
      float fog = smoothstep(6.0, 33.0, length(vWorld.xz)) * 0.34;
      gl_FragColor = vec4(mix(shaded, vec3(0.91, 0.92, 0.87), fog), 1.0);
    }`;

  function create(canvas, options = {}) {
    if (!canvas || typeof canvas.getContext !== 'function') throw new TypeError('ChemLeap requires a canvas.');
    const onStatus = text => { if (typeof options.onStatus === 'function') options.onStatus(text); };
    let gl = null, context2d = null, program = null, locations = null, meshes = {}, disposed = false, lost = false;
    let width = 1, height = 1, dpr = 1, viewProjection = null, clock = 0, previousTime = null;
    let scene = { state: { current: 'H2', platforms: [], phase: 'ready' }, species: {}, selected: -1, charge: 0,
      jumpProgress: 0, jump: null, paused: false, reducedMotion: false };
    let previousPhase = 'ready', previousSubstance = null, reactionTime = -100, selectedByPointer = -1;
    let resizeObserver = null, mode = 'unavailable';
    const contextAttributes = { alpha: false, antialias: true, depth: true, stencil: false, preserveDrawingBuffer: false };
    try { gl = canvas.getContext('webgl', contextAttributes); } catch (_) { /* Honest 2D fallback below. */ }

    function shader(type, source) {
      const compiled = gl.createShader(type);
      if (!compiled) throw new Error('Unable to create WebGL shader.');
      gl.shaderSource(compiled, source); gl.compileShader(compiled);
      if (!gl.getShaderParameter(compiled, gl.COMPILE_STATUS)) {
        const detail = gl.getShaderInfoLog(compiled); gl.deleteShader(compiled);
        throw new Error('Local 3D shader compilation failed: ' + (detail || 'unknown'));
      }
      return compiled;
    }
    function initializeGL() {
      const vertex = shader(gl.VERTEX_SHADER, vertexSource), fragment = shader(gl.FRAGMENT_SHADER, fragmentSource);
      program = gl.createProgram();
      if (!program) { gl.deleteShader(vertex); gl.deleteShader(fragment); throw new Error('Unable to create 3D program.'); }
      gl.attachShader(program, vertex); gl.attachShader(program, fragment); gl.linkProgram(program);
      gl.deleteShader(vertex); gl.deleteShader(fragment);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error('Local 3D shader linking failed.');
      locations = {
        position: gl.getAttribLocation(program, 'aPosition'), normal: gl.getAttribLocation(program, 'aNormal'),
        viewProjection: gl.getUniformLocation(program, 'uViewProjection'), model: gl.getUniformLocation(program, 'uModel'),
        normalMatrix: gl.getUniformLocation(program, 'uNormal'), color: gl.getUniformLocation(program, 'uColor'), unlit: gl.getUniformLocation(program, 'uUnlit')
      };
      meshes = {};
      for (const [name, data] of Object.entries(geometry)) {
        const buffer = gl.createBuffer();
        if (!buffer) throw new Error('Unable to allocate local 3D geometry.');
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer); gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
        meshes[name] = { buffer, count: data.length / 6 };
      }
      gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LEQUAL);
      // Both sides render; depth still occludes rear geometry and avoids mirrored mesh surprises.
      gl.disable(gl.CULL_FACE); gl.clearColor(0.91, 0.92, 0.87, 1);
      mode = 'webgl'; canvas.dataset.renderer = 'webgl';
    }
    if (gl) {
      try { initializeGL(); onStatus('本地 WebGL 立体场景'); }
      catch (error) { onStatus('3D 绘图初始化失败，请重载游戏。'); disposeResources(); throw error; }
    } else {
      context2d = canvas.getContext('2d');
      if (!context2d) { onStatus('当前设备无法创建绘图画布，请更换支持 Canvas 的环境。'); throw new Error('Canvas unavailable.'); }
      mode = 'canvas2d'; canvas.dataset.renderer = 'canvas2d';
      onStatus('WebGL 不可用，已使用 Canvas 2D 透视降级画面。');
    }

    function platforms() { return Array.isArray(scene.state.platforms) ? scene.state.platforms : []; }
    function updateCamera() {
      const points = [{ x: 0, z: 2 }, ...platforms()];
      const minX = Math.min(-2, ...points.map(point => Number(point.x) || 0));
      const maxX = Math.max(2, ...points.map(point => Number(point.x) || 0));
      const minZ = Math.min(-2, ...points.map(point => Number(point.z) || 0));
      const maxZ = Math.max(2, ...points.map(point => Number(point.z) || 0));
      const target = [(minX + maxX) * 0.5, 0, (minZ + maxZ) * 0.5 - 0.1];
      const aspect = width / height;
      const span = Math.max((maxX - minX + 4.7) / Math.min(aspect, 1.7), (maxZ - minZ + 4.6) * 0.88, 9);
      const distance = span * 1.16;
      const eye = add(target, [distance * 0.30, distance * 0.74, distance * 0.91]);
      viewProjection = multiply(perspective(aspect, distance), lookAt(eye, target));
    }
    function resize() {
      if (disposed) return;
      const bounds = canvas.getBoundingClientRect();
      width = Math.max(1, bounds.width || canvas.clientWidth || 1);
      height = Math.max(1, bounds.height || canvas.clientHeight || 1);
      dpr = Math.min(2, Math.max(1, window.devicePixelRatio || 1));
      const bufferWidth = Math.round(width * dpr), bufferHeight = Math.round(height * dpr);
      if (canvas.width !== bufferWidth || canvas.height !== bufferHeight) { canvas.width = bufferWidth; canvas.height = bufferHeight; }
      if (gl && !lost) gl.viewport(0, 0, canvas.width, canvas.height);
      updateCamera();
    }
    function project(x, y, z) {
      const matrix = viewProjection;
      const w = matrix[3] * x + matrix[7] * y + matrix[11] * z + matrix[15];
      const px = matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12];
      const py = matrix[1] * x + matrix[5] * y + matrix[9] * z + matrix[13];
      const depth = matrix[2] * x + matrix[6] * y + matrix[10] * z + matrix[14];
      return { x: (px / w * 0.5 + 0.5) * width, y: (0.5 - py / w * 0.5) * height,
        depth: depth / w, visible: w > 0 && Math.abs(px / w) < 1.2 && Math.abs(py / w) < 1.2 };
    }
    function projectPlatforms() {
      if (!viewProjection) updateCamera();
      return platforms().map((platform, index) => {
        const x = Number(platform.x) || 0, z = Number(platform.z) || 0;
        const center = project(x, 0.44, z), edge = project(x + 0.72, 0.44, z);
        return { index, id: platform.id, substance: platform.substance, ...center,
          radius: Math.max(14, Math.hypot(center.x - edge.x, center.y - edge.y) * 1.3) };
      });
    }
    function draw(name, position, scale, tint, angle = 0, unlit = false) {
      const mesh = meshes[name];
      gl.bindBuffer(gl.ARRAY_BUFFER, mesh.buffer);
      gl.vertexAttribPointer(locations.position, 3, gl.FLOAT, false, 24, 0);
      gl.vertexAttribPointer(locations.normal, 3, gl.FLOAT, false, 24, 12);
      gl.uniformMatrix4fv(locations.model, false, transform(position, scale, angle));
      gl.uniformMatrix3fv(locations.normalMatrix, false, normalTransform(scale, angle));
      gl.uniform3fv(locations.color, tint); gl.uniform1f(locations.unlit, unlit ? 1 : 0);
      gl.drawArrays(gl.TRIANGLES, 0, mesh.count);
    }
    function drawLine(from, to, tint, thickness = 0.032) {
      const direction = sub(to, from), length = Math.max(0.001, Math.hypot(direction[0], direction[2]));
      draw('box', from.map((value, index) => mix(value, to[index], 0.5)), [thickness, thickness, length], tint,
        Math.atan2(direction[0], direction[2]), true);
    }
    function drawPlatform(platform, index) {
      const selected = Number(scene.selected) === index;
      const tint = color(scene.species[platform.substance]?.color);
      const x = Number(platform.x) || 0, z = Number(platform.z) || 0;
      draw('disc', [x + 0.17, -0.495, z + 0.15], [1.07, 1, 0.88], palette.shadow, 0, true);
      draw('box', [x, -0.09, z], [1.3, 0.72, 1.18], tint);
      draw('box', [x, 0.31, z], [1.38, 0.14, 1.26], selected ? palette.orange : palette.cream);
      draw('box', [x, -0.40, z], [1.15, 0.11, 1.04], palette.dark);
      draw('disc', [x, 0.388, z], [0.27, 1, 0.27], selected ? palette.white : tint, 0, true);
      draw('sphere', [x, 0.455, z], [0.085, 0.085, 0.085], selected ? palette.orange : palette.cream);
      if (selected) {
        const pulse = scene.reducedMotion ? 1 : 1 + Math.sin(clock * 0.003) * 0.05;
        for (let mark = 0; mark < 4; mark++) {
          const angle = mark * Math.PI / 2;
          draw('box', [x + Math.cos(angle) * 0.89 * pulse, -0.47, z + Math.sin(angle) * 0.89 * pulse],
            [0.20, 0.025, 0.07], palette.orange, -angle, true);
        }
      }
    }
    function characterPosition() {
      const progress = clamp(scene.jumpProgress, 0, 1);
      if (scene.state.phase === 'jumping' && scene.jump) {
        const from = scene.jump.from || { x: 0, z: 2 }, to = scene.jump.to || from;
        const smooth = progress * progress * (3 - 2 * progress);
        return [mix(Number(from.x) || 0, Number(to.x) || 0, smooth),
          1.05 + Math.sin(progress * Math.PI) * clamp(scene.jump.height ?? 2.4, 0.3, 5),
          mix(Number(from.z) || 0, Number(to.z) || 0, smooth)];
      }
      const bob = scene.reducedMotion || scene.paused ? 0 : Math.sin(clock * 0.0025) * 0.045;
      return [0, 1.05 + bob - clamp(scene.charge, 0, 1) * 0.11, 2];
    }
    function renderGL() {
      gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT); gl.useProgram(program);
      gl.enableVertexAttribArray(locations.position); gl.enableVertexAttribArray(locations.normal);
      gl.uniformMatrix4fv(locations.viewProjection, false, viewProjection);
      draw('box', [0, -0.57, -3], [60, 0.12, 60], palette.floor);
      // Small inlaid grid: spatial depth without a visually noisy checkerboard.
      for (let row = -12; row <= 8; row += 2) {
        drawLine([-11, -0.502, row], [11, -0.502, row], color('#d7dfd0'), 0.012);
      }
      for (let column = -10; column <= 10; column += 2) {
        drawLine([column, -0.501, -13], [column, -0.501, 9], color('#d7dfd0'), 0.012);
      }
      platforms().forEach((platform, index) => {
        const tint = index === Number(scene.selected) ? palette.orange : palette.line;
        const from = [0, -0.48, 2], to = [Number(platform.x) || 0, -0.48, Number(platform.z) || 0];
        for (let part = 0; part < 7; part++) {
          const start = from.map((value, axis) => mix(value, to[axis], 0.15 + part * 0.115));
          const end = from.map((value, axis) => mix(value, to[axis], 0.19 + part * 0.115));
          drawLine(start, end, tint, 0.025);
        }
      });
      drawPlatform({ x: 0, z: 2, substance: scene.state.current }, -2);
      platforms().forEach(drawPlatform);
      const position = characterPosition(), jumping = scene.state.phase === 'jumping';
      const tint = color(scene.species[scene.state.current]?.color, '#e5a16d');
      const squash = jumping ? 1 : 1 - clamp(scene.charge, 0, 1) * 0.16;
      const shadowScale = Math.max(0.20, 0.47 - (position[1] - 1) * 0.06);
      draw('disc', [position[0] + 0.12, 0.397, position[2] + 0.10], [shadowScale, 1, shadowScale * 0.69], color('#bbbfab'), 0, true);
      const rotation = scene.reducedMotion ? 0 : clock * 0.0004;
      draw('sphere', position, [0.42 / squash, 0.43 * squash, 0.42 / squash], tint, rotation);
      const satellites = [
        [Math.cos(rotation) * 0.47, 0.18, Math.sin(rotation) * 0.47],
        [-Math.cos(rotation) * 0.38, -0.12, -Math.sin(rotation) * 0.38]
      ];
      for (let index = 0; index < satellites.length; index++) {
        const point = add(position, satellites[index]);
        draw('sphere', point, [0.18, 0.18, 0.18], index === 0 ? palette.cream : palette.orange);
      }
      // Front-facing dark insets give the original molecule a restrained character.
      draw('sphere', add(position, [-0.10, 0.07, 0.39]), [0.038, 0.055, 0.035], palette.dark);
      draw('sphere', add(position, [0.11, 0.07, 0.39]), [0.038, 0.055, 0.035], palette.dark);
      if (!scene.reducedMotion) {
        for (let index = 0; index < 10; index++) {
          const theta = rotation * 1.8 + index / 10 * TAU;
          draw('sphere', add(position, [Math.cos(theta) * 0.72, Math.sin(theta) * 0.22, Math.sin(theta) * 0.4]),
            [0.025, 0.025, 0.025], palette.cream);
        }
      }
      const age = (clock - reactionTime) / 900;
      if (age >= 0 && age < 1 && !scene.reducedMotion) {
        for (let index = 0; index < 18; index++) {
          const angle = index / 18 * TAU, radius = 0.4 + age * (1.1 + index % 3 * 0.15);
          const size = (1 - age) * 0.07;
          draw('sphere', [Math.cos(angle) * radius, 0.8 + Math.sin(age * Math.PI) * (0.7 + index % 4 * 0.2), 2 + Math.sin(angle) * radius],
            [size, size, size], index % 2 ? palette.orange : palette.jade);
        }
      }
    }
    function polygon2D(points, fill) {
      context2d.beginPath();
      points.forEach((point, index) => { const p = project(...point); if (index) context2d.lineTo(p.x, p.y); else context2d.moveTo(p.x, p.y); });
      context2d.closePath(); context2d.fillStyle = fill; context2d.fill();
    }
    function render2D() {
      const context = context2d;
      context.setTransform(dpr, 0, 0, dpr, 0, 0); context.clearRect(0, 0, width, height);
      const background = context.createLinearGradient(0, 0, 0, height);
      background.addColorStop(0, '#f1f0e5'); background.addColorStop(1, '#dce3d4');
      context.fillStyle = background; context.fillRect(0, 0, width, height);
      const items = [{ x: 0, z: 2, substance: scene.state.current, index: -2 }, ...platforms().map((p, index) => ({ ...p, index }))]
        .sort((a, b) => project(b.x, 0, b.z).depth - project(a.x, 0, a.z).depth);
      for (const item of items) {
        const x = Number(item.x) || 0, z = Number(item.z) || 0, sx = 0.69, sz = 0.63;
        const top = [[x - sx, 0.38, z - sz], [x + sx, 0.38, z - sz], [x + sx, 0.38, z + sz], [x - sx, 0.38, z + sz]];
        polygon2D([[x - sx, -0.45, z + sz], [x + sx, -0.45, z + sz], top[2], top[3]], '#4f7e6a');
        polygon2D([[x + sx, -0.45, z + sz], [x + sx, -0.45, z - sz], top[1], top[2]], '#6a9b83');
        polygon2D(top, Number(scene.selected) === item.index ? '#e6a36a' : '#fff0d3');
        const dotPosition = project(x, 0.4, z);
        context.fillStyle = scene.species[item.substance]?.color || '#73a593'; context.beginPath();
        context.ellipse(dotPosition.x, dotPosition.y, 5, 3, 0, 0, TAU); context.fill();
      }
      const position = characterPosition(), p = project(...position), edge = project(position[0] + 0.43, position[1], position[2]);
      const radius = Math.max(9, Math.hypot(edge.x - p.x, edge.y - p.y));
      const gradient = context.createRadialGradient(p.x - radius * 0.3, p.y - radius * 0.3, 1, p.x, p.y, radius);
      gradient.addColorStop(0, '#fff0ce'); gradient.addColorStop(1, scene.species[scene.state.current]?.color || '#dc9c65');
      context.fillStyle = gradient; context.beginPath(); context.arc(p.x, p.y, radius, 0, TAU); context.fill();
      context.fillStyle = '#3a6b5b';
      for (const offset of [-0.22, 0.22]) { context.beginPath(); context.arc(p.x + offset * radius, p.y, Math.max(1.5, radius * 0.07), 0, TAU); context.fill(); }
    }
    function render(timeMs) {
      if (disposed || lost) return;
      const now = Number.isFinite(timeMs) ? timeMs : performance.now();
      if (previousTime !== null && !scene.paused) clock += clamp(now - previousTime, 0, 100);
      previousTime = now;
      if (!viewProjection) resize();
      if (mode === 'webgl') renderGL(); else render2D();
    }
    function setScene(next) {
      if (disposed || !next || typeof next !== 'object') return;
      scene = { ...scene, ...next };
      if (!scene.state || typeof scene.state !== 'object') scene.state = { current: 'H2', platforms: [], phase: 'ready' };
      if (!scene.species || typeof scene.species !== 'object') scene.species = {};
      if ((previousPhase === 'jumping' && scene.state.phase === 'ready') ||
          (previousSubstance !== null && previousSubstance !== scene.state.current)) reactionTime = clock;
      previousPhase = scene.state.phase; previousSubstance = scene.state.current;
      updateCamera();
    }
    function selectAt(event) {
      if (disposed || lost || scene.paused || scene.state.phase !== 'ready') return;
      const bounds = canvas.getBoundingClientRect(), x = event.clientX - bounds.left, y = event.clientY - bounds.top;
      const candidates = projectPlatforms().filter(point => point.visible && Math.hypot(point.x - x, point.y - y) <= Math.max(point.radius, 24));
      candidates.sort((a, b) => Math.hypot(a.x - x, a.y - y) - Math.hypot(b.x - x, b.y - y));
      if (!candidates.length) return;
      const index = candidates[0].index;
      if (selectedByPointer === index && Number(scene.selected) === index && event.detail >= 2) {
        if (typeof options.onActivate === 'function') options.onActivate(index);
      } else if (typeof options.onSelect === 'function') options.onSelect(index);
      selectedByPointer = index;
    }
    function contextLost(event) {
      event.preventDefault(); lost = true; previousTime = null;
      canvas.dataset.renderer = 'context-lost';
      onStatus('WebGL 绘图上下文已丢失，画面暂停；恢复后可继续。');
      if (typeof options.onContextLost === 'function') options.onContextLost();
    }
    function contextRestored() {
      if (disposed) return;
      try {
        initializeGL(); lost = false; previousTime = null; resize();
        onStatus('本地 WebGL 立体场景已恢复。');
        if (typeof options.onContextRestored === 'function') options.onContextRestored();
      } catch (_) { lost = true; onStatus('3D 绘图恢复失败，请重载游戏。'); }
    }
    function disposeResources() {
      if (gl && !gl.isContextLost()) {
        for (const mesh of Object.values(meshes)) gl.deleteBuffer(mesh.buffer);
        if (program) gl.deleteProgram(program);
      }
      meshes = {}; program = null;
    }
    function dispose() {
      if (disposed) return;
      disposed = true; canvas.removeEventListener('click', selectAt);
      canvas.removeEventListener('webglcontextlost', contextLost); canvas.removeEventListener('webglcontextrestored', contextRestored);
      window.removeEventListener('resize', resize); if (resizeObserver) resizeObserver.disconnect();
      disposeResources();
    }
    canvas.addEventListener('click', selectAt);
    canvas.addEventListener('webglcontextlost', contextLost, false); canvas.addEventListener('webglcontextrestored', contextRestored, false);
    window.addEventListener('resize', resize);
    if (typeof ResizeObserver === 'function') { resizeObserver = new ResizeObserver(resize); resizeObserver.observe(canvas); }
    resize();
    return Object.freeze({ setScene, render, resize, projectPlatforms, dispose, get mode() { return mode; }, get contextLost() { return lost; } });
  }
  window.ChemLeapRenderer = Object.freeze({ create });
}());
