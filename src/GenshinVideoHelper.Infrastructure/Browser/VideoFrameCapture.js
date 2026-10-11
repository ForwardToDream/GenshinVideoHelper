(() => {
  const request = __REQUEST__;
  const bv = location.pathname.match(/\/video\/(BV[0-9A-Za-z]+)/)?.[1];
  const part = Number(new URL(location.href).searchParams.get('p') || 1);
  if (bv !== request.bvid || part !== request.part) return null;
  // The active PiP element is precisely the followed main video, never a page advertisement.
  const v = document.pictureInPictureElement;
  if (!(v instanceof HTMLVideoElement) || !v.isConnected || v.readyState < 2 || v.seeking || !v.videoWidth) return null;
  let state = window.__gvhVision;
  if (state?.format !== 'rgba8') state = window.__gvhVision = {format:'rgba8', id:crypto.randomUUID(), epoch:0, video:null, canvas:document.createElement('canvas')};
  if (state.video !== v) {
    state.video = v; state.epoch++;
    for (const event of ['seeking','emptied','loadedmetadata','resize'])
      v.addEventListener(event, () => { if (state.video === v) state.epoch++; });
  }
  const w = v.videoWidth, h = v.videoHeight;
  const epoch = `${state.id}:${state.epoch}:${w}:${h}`;
  const bounds = {x:0,y:0,width:Math.floor(w/3),height:Math.floor(h/2)};
  let r = bounds;
  const planned = request.region;
  if (request.epoch === epoch && planned && planned.x >= 0 && planned.y >= 0 &&
      planned.width > 0 && planned.height > 0 && planned.x+planned.width <= bounds.width && planned.y+planned.height <= bounds.height)
    r = planned;
  if (r.width <= 0 || r.height <= 0) return null;
  if (r.width*r.height > 3000000) throw new Error('视频搜索区域过大，请使用 4K 或以下清晰度');
  // Preserve native decoded pixels: disk/arrow localization runs on the guide source, never the small PiP.
  const cw = r.width, ch = r.height;
  const c = state.canvas;
  if (c.width !== cw || c.height !== ch) { c.width=cw; c.height=ch; }
  // Reuse identical paused frames, but always recheck element, epoch, PiP and URL above.
  const frameNumber = typeof v.getVideoPlaybackQuality === 'function' ? v.getVideoPlaybackQuality().totalVideoFrames : v.currentTime;
  const key = `${epoch}:${frameNumber}:${JSON.stringify(r)}`;
  if (key !== state.key) {
    const ctx = c.getContext('2d', {alpha:false, willReadFrequently:true});
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(v,r.x,r.y,r.width,r.height,0,0,cw,ch);
    const bytes = new Uint8Array(ctx.getImageData(0,0,cw,ch).data.buffer);
    if (typeof bytes.toBase64 === 'function') state.pixels = bytes.toBase64();
    else {
      const chunks = [];
      for (let offset=0; offset<bytes.length; offset+=32768)
        chunks.push(String.fromCharCode(...bytes.subarray(offset,offset+32768)));
      state.pixels = btoa(chunks.join(''));
    }
    state.key = key;
  }
  // An acknowledged paused sample needs only metadata; C# already owns the decoded pixels.
  return {width:w,height:h,region:r,epoch,paused:v.paused,time:v.currentTime,frameKey:key,
    pixels:request.frameKey === key ? '' : state.pixels};
})()
