// Druckkammer Interactive 3D STL/OBJ Viewer & Auto-Thumbnail Generator
const instances = new Map();

export async function initViewer(containerId, modelUrl, dotNetHelper, autoCaptureThumbnail) {
    disposeViewer(containerId);
    const container = document.getElementById(containerId);
    if (!container) return;

    container.innerHTML = '';
    const width = container.clientWidth || 520;
    const height = container.clientHeight || 380;

    // Parse binary/ASCII STL directly in JS so it works both with Three.js and native WebGL/Canvas fallback
    let meshData = null;
    try {
        const resp = await fetch(modelUrl);
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        const buffer = await resp.arrayBuffer();
        meshData = parseStlArrayBuffer(buffer);
    } catch (err) {
        console.warn("Konnte 3D-Datei nicht laden:", err);
        container.innerHTML = `<div style="display:flex;align-items:center;justify-content:center;height:100%;color:#B0BEC5;font-size:0.9rem;">3D-Vorschau konnte nicht geladen werden</div>`;
        return;
    }

    const canvas = document.createElement('canvas');
    canvas.width = width * (window.devicePixelRatio || 1);
    canvas.height = height * (window.devicePixelRatio || 1);
    canvas.style.width = '100%';
    canvas.style.height = '100%';
    canvas.style.display = 'block';
    canvas.style.cursor = 'grab';
    container.appendChild(canvas);

    const state = {
        canvas,
        meshData,
        rotX: -0.55,
        rotZ: 0.65,
        zoom: 1.0,
        wireframe: false,
        autoRotate: true,
        isDragging: false,
        lastX: 0,
        lastY: 0,
        animId: null,
        dotNetHelper
    };

    // Mouse / Touch Orbit Controls
    canvas.addEventListener('pointerdown', (e) => {
        state.isDragging = true;
        state.autoRotate = false;
        state.lastX = e.clientX;
        state.lastY = e.clientY;
        canvas.style.cursor = 'grabbing';
        canvas.setPointerCapture(e.pointerId);
    });

    canvas.addEventListener('pointermove', (e) => {
        if (!state.isDragging) return;
        const dx = e.clientX - state.lastX;
        const dy = e.clientY - state.lastY;
        state.lastX = e.clientX;
        state.lastY = e.clientY;
        state.rotZ += dx * 0.012;
        state.rotX = Math.max(-1.45, Math.min(0.2, state.rotX + dy * 0.012));
        renderFrame(state);
    });

    canvas.addEventListener('pointerup', (e) => {
        state.isDragging = false;
        canvas.style.cursor = 'grab';
    });

    canvas.addEventListener('wheel', (e) => {
        e.preventDefault();
        const factor = e.deltaY < 0 ? 1.1 : 0.9;
        state.zoom = Math.max(0.35, Math.min(3.5, state.zoom * factor));
        renderFrame(state);
    }, { passive: false });

    instances.set(containerId, state);

    renderFrame(state);

    if (autoCaptureThumbnail && dotNetHelper) {
        setTimeout(async () => {
            try {
                const dataUrl = canvas.toDataURL('image/png');
                await dotNetHelper.invokeMethodAsync('OnAutoThumbnailCaptured', dataUrl);
            } catch (e) {
                console.warn('Auto-Thumbnail Fehler:', e);
            }
        }, 250);
    }

    function animate() {
        if (!instances.has(containerId)) return;
        if (state.autoRotate && !state.isDragging) {
            state.rotZ += 0.008;
            renderFrame(state);
        }
        state.animId = requestAnimationFrame(animate);
    }
    state.animId = requestAnimationFrame(animate);
}

export function setViewerOptions(containerId, wireframe, autoRotate, resetView) {
    const state = instances.get(containerId);
    if (!state) return;
    if (resetView) {
        state.rotX = -0.55;
        state.rotZ = 0.65;
        state.zoom = 1.0;
    }
    state.wireframe = wireframe;
    state.autoRotate = autoRotate;
    renderFrame(state);
}

export function captureSnapshotDataUrl(containerId) {
    const state = instances.get(containerId);
    if (!state || !state.canvas) return null;
    renderFrame(state);
    return state.canvas.toDataURL('image/png');
}

export function disposeViewer(containerId) {
    const state = instances.get(containerId);
    if (state) {
        if (state.animId) cancelAnimationFrame(state.animId);
        instances.delete(containerId);
    }
}

function parseStlArrayBuffer(buffer) {
    const view = new DataView(buffer);
    if (buffer.byteLength < 84) return { triangles: [], radius: 20, center: [0, 0, 0] };

    const triCount = view.getUint32(80, true);
    const expectedByteLen = 84 + triCount * 50;
    const triangles = [];

    let minX = Infinity, minY = Infinity, minZ = Infinity;
    let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;

    if (triCount > 0 && expectedByteLen <= buffer.byteLength + 64) {
        // Binary STL
        const stride = Math.max(1, Math.floor(triCount / 6000)); // Decimate huge meshes for smooth 60fps preview
        for (let i = 0; i < triCount; i += stride) {
            const offset = 84 + i * 50;
            if (offset + 50 > buffer.byteLength) break;

            const v1 = [view.getFloat32(offset + 12, true), view.getFloat32(offset + 16, true), view.getFloat32(offset + 20, true)];
            const v2 = [view.getFloat32(offset + 24, true), view.getFloat32(offset + 28, true), view.getFloat32(offset + 32, true)];
            const v3 = [view.getFloat32(offset + 36, true), view.getFloat32(offset + 40, true), view.getFloat32(offset + 44, true)];

            for (const v of [v1, v2, v3]) {
                if (v[0] < minX) minX = v[0]; if (v[0] > maxX) maxX = v[0];
                if (v[1] < minY) minY = v[1]; if (v[1] > maxY) maxY = v[1];
                if (v[2] < minZ) minZ = v[2]; if (v[2] > maxZ) maxZ = v[2];
            }
            triangles.push([v1, v2, v3]);
        }
    } else {
        // ASCII STL fallback
        const text = new TextDecoder().decode(buffer);
        const vertexRegex = /vertex\s+([+-]?\d*\.?\d+(?:[eE][+-]?\d+)?)\s+([+-]?\d*\.?\d+(?:[eE][+-]?\d+)?)\s+([+-]?\d*\.?\d+(?:[eE][+-]?\d+)?)/gi;
        const verts = [];
        let match;
        while ((match = vertexRegex.exec(text)) !== null) {
            const v = [parseFloat(match[1]), parseFloat(match[2]), parseFloat(match[3])];
            if (v[0] < minX) minX = v[0]; if (v[0] > maxX) maxX = v[0];
            if (v[1] < minY) minY = v[1]; if (v[1] > maxY) maxY = v[1];
            if (v[2] < minZ) minZ = v[2]; if (v[2] > maxZ) maxZ = v[2];
            verts.push(v);
            if (verts.length === 3) {
                triangles.push([verts[0], verts[1], verts[2]]);
                verts.length = 0;
            }
        }
    }

    if (triangles.length === 0) {
        return { triangles: [], radius: 20, center: [0, 0, 0] };
    }

    const cx = (minX + maxX) / 2;
    const cy = (minY + maxY) / 2;
    const cz = (minZ + maxZ) / 2;
    const dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
    const radius = Math.max(1, Math.sqrt(dx * dx + dy * dy + dz * dz) / 2);

    return { triangles, center: [cx, cy, cz], minZ, radius, dims: [dx, dy, dz] };
}

function renderFrame(state) {
    const { canvas, meshData, rotX, rotZ, zoom, wireframe } = state;
    const ctx = canvas.getContext('2d');
    const w = canvas.width;
    const h = canvas.height;

    // Background gradient (Resin Slicer Studio Dark look)
    const grad = ctx.createRadialGradient(w * 0.5, h * 0.45, w * 0.05, w * 0.5, h * 0.5, w * 0.75);
    grad.addColorStop(0, '#262B38');
    grad.addColorStop(1, '#141720');
    ctx.fillStyle = grad;
    ctx.fillRect(0, 0, w, h);

    if (!meshData || meshData.triangles.length === 0) return;

    const [cx, cy, cz] = meshData.center;
    const scale = (Math.min(w, h) * 0.40 * zoom) / meshData.radius;

    const cosZ = Math.cos(rotZ), sinZ = Math.sin(rotZ);
    const cosX = Math.cos(rotX), sinX = Math.sin(rotX);

    function project(x, y, z) {
        const rx = (x - cx) * cosZ - (y - cy) * sinZ;
        const ry = (x - cx) * sinZ + (y - cy) * cosZ;
        const rz = z - cz;

        const fy = ry * cosX - rz * sinX;
        const fz = ry * sinX + rz * cosX;

        return [w * 0.5 + rx * scale, h * 0.54 - fz * scale, fy];
    }

    // Draw buildplate reference grid at bottom (minZ)
    ctx.strokeStyle = 'rgba(126, 87, 194, 0.25)';
    ctx.lineWidth = 1;
    const gridR = meshData.radius * 1.1;
    const steps = 6;
    for (let i = -steps; i <= steps; i++) {
        const offset = (i / steps) * gridR;
        const p1 = project(cx - gridR, cy + offset, meshData.minZ);
        const p2 = project(cx + gridR, cy + offset, meshData.minZ);
        ctx.beginPath(); ctx.moveTo(p1[0], p1[1]); ctx.lineTo(p2[0], p2[1]); ctx.stroke();

        const p3 = project(cx + offset, cy - gridR, meshData.minZ);
        const p4 = project(cx + offset, cy + gridR, meshData.minZ);
        ctx.beginPath(); ctx.moveTo(p3[0], p3[1]); ctx.lineTo(p4[0], p4[1]); ctx.stroke();
    }

    // Transform & sort triangles (Painter's algorithm with directional studio shading)
    const projectedTris = [];
    const lightDir = normalize([0.45, -0.65, 0.62]);

    for (const tri of meshData.triangles) {
        const p0 = project(tri[0][0], tri[0][1], tri[0][2]);
        const p1 = project(tri[1][0], tri[1][1], tri[1][2]);
        const p2 = project(tri[2][0], tri[2][1], tri[2][2]);

        // Rotate vertices for lighting normal
        const u = [tri[1][0] - tri[0][0], tri[1][1] - tri[0][1], tri[1][2] - tri[0][2]];
        const v = [tri[2][0] - tri[0][0], tri[2][1] - tri[0][1], tri[2][2] - tri[0][2]];
        const n = normalize([
            u[1] * v[2] - u[2] * v[1],
            u[2] * v[0] - u[0] * v[2],
            u[0] * v[1] - u[1] * v[0]
        ]);

        // Rotate normal into view space
        const nrx = n[0] * cosZ - n[1] * sinZ;
        const nry = n[0] * sinZ + n[1] * cosZ;
        const nfz = nry * sinX + n[2] * cosX;
        const nfy = nry * cosX - n[2] * sinX;

        const intensity = Math.max(0.18, Math.min(1.0, 0.35 + 0.65 * Math.abs(nrx * lightDir[0] + nfy * lightDir[1] + nfz * lightDir[2])));
        const avgDepth = (p0[2] + p1[2] + p2[2]) / 3.0;

        projectedTris.push({ p0, p1, p2, avgDepth, intensity });
    }

    projectedTris.sort((a, b) => b.avgDepth - a.avgDepth);

    // Sculpt Resin Grey-Purple Tint
    for (const t of projectedTris) {
        ctx.beginPath();
        ctx.moveTo(t.p0[0], t.p0[1]);
        ctx.lineTo(t.p1[0], t.p1[1]);
        ctx.lineTo(t.p2[0], t.p2[1]);
        ctx.closePath();

        if (wireframe) {
            ctx.strokeStyle = 'rgba(38, 166, 154, 0.75)';
            ctx.lineWidth = 0.8;
            ctx.stroke();
        } else {
            const r = Math.round(95 + t.intensity * 105);
            const g = Math.round(105 + t.intensity * 110);
            const b = Math.round(125 + t.intensity * 115);
            ctx.fillStyle = `rgb(${r},${g},${b})`;
            ctx.fill();
            ctx.strokeStyle = `rgba(${r},${g},${b},0.4)`;
            ctx.lineWidth = 0.4;
            ctx.stroke();
        }
    }
}

function normalize(v) {
    const len = Math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) || 1;
    return [v[0] / len, v[1] / len, v[2] / len];
}
