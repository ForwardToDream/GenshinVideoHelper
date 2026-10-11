(() => {
  const request = __REQUEST__;
  const bv = location.pathname.match(/\/video\/(BV[0-9A-Za-z]+)/)?.[1];
  const part = Number(new URL(location.href).searchParams.get('p') || 1);
  if (bv !== request.bvid || part !== request.part) return null;
  // The active PiP element is precisely the followed main video, never a page advertisement.
  const v = document.pictureInPictureElement;
  if (!(v instanceof HTMLVideoElement) || !v.isConnected || v.readyState < 2 || v.seeking || !v.videoWidth) return null;
  let state = window.__gvhVision;
  if (!state) state = window.__gvhVision = {id:crypto.randomUUID(), epoch:0, video:null, canvas:document.createElement('canvas')};
  if (state.video !== v) {
    state.video = v; state.epoch++;
    for (const event of ['seeking','emptied','loadedmetadata','resize'])
      v.addEventListener(event, () => { if (state.video === v) state.epoch++; });
  }
  const w = v.videoWidth, h = v.videoHeight;
  const epoch = `${state.id}:${state.epoch}:${w}:${h}`;
  const r = {x:0,y:0,width:Math.floor(w/3),height:Math.floor(h/2)};
  if (r.width <= 0 || r.height <= 0) return null;
  // Preserve native decoded pixels: disk/arrow localization runs on the guide source, never the small PiP.
  const cw = r.width, ch = r.height;
  const c = state.canvas;
  if (c.width !== cw || c.height !== ch) { c.width=cw; c.height=ch; }
  // Reuse identical paused frames, but always recheck element, epoch, PiP and URL above.
  const key = `${epoch}:${v.currentTime}:${JSON.stringify(r)}`;
  if (!(v.paused && key === state.key)) {
    const ctx = c.getContext('2d', {alpha:false});
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(v,r.x,r.y,r.width,r.height,0,0,cw,ch);
    state.png = c.toDataURL('image/png').split(',')[1]; state.key = key;
  }
  return {width:w,height:h,region:r,epoch,paused:v.paused,time:v.currentTime,png:state.png};
})()
