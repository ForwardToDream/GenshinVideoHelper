(async () => {
  const command = __COMMAND__;
  const videos = [...document.querySelectorAll('video')]
    .filter(video => video.isConnected && video.readyState >= 1);
  const score = video => {
    const rect = video.getBoundingClientRect();
    const visible = getComputedStyle(video).visibility !== 'hidden' &&
      getComputedStyle(video).display !== 'none';
    return (video === document.pictureInPictureElement ? 1e12 : 0) +
      (visible ? rect.width * rect.height : 0) + (!video.paused ? 1 : 0);
  };
  const video = videos.sort((a, b) => score(b) - score(a))[0];
  if (!video || document.readyState === 'loading') throw new Error('VIDEO_NOT_READY: 视频尚未加载，请在浏览器中完成登录或等待视频加载。');

  switch (command.action) {
    case 'status': break;
    case 'ensurePlay': if (video.paused) await video.play(); break;
    case 'ensurePip':
      if (document.pictureInPictureElement !== video) {
        if (!document.pictureInPictureEnabled || !video.requestPictureInPicture)
          throw new Error('浏览器当前不支持画中画，请使用桌面版 Chrome。');
        video.disablePictureInPicture = false;
        await video.requestPictureInPicture();
      }
      break;
    case 'ensurePipClosed':
      if (document.pictureInPictureElement) await document.exitPictureInPicture();
      break;
    case 'toggle':
      if (video.paused) await video.play(); else video.pause();
      break;
    case 'seek': {
      if (!Number.isFinite(video.duration))
        throw new Error('当前视频不能快进或后退，请使用普通点播视频。');
      const target = command.absolute ? command.value : video.currentTime + command.value;
      video.currentTime = Math.max(0, Math.min(target, Math.max(0, video.duration - 0.05)));
      break;
    }
    case 'mute': video.muted = !video.muted; break;
    case 'rate': video.playbackRate = Math.max(0.25, Math.min(3, command.value)); break;
    case 'pip':
      if (document.pictureInPictureElement) {
        await document.exitPictureInPicture();
      } else {
        if (!document.pictureInPictureEnabled || !video.requestPictureInPicture)
          throw new Error('浏览器当前不支持画中画，请使用桌面版 Chrome。');
        video.disablePictureInPicture = false;
        await video.requestPictureInPicture();
      }
      break;
    default: throw new Error('未知的视频操作。');
  }

  return {
    title: document.title,
    url: location.href,
    paused: video.paused,
    currentTime: video.currentTime,
    duration: Number.isFinite(video.duration) ? video.duration : null,
    muted: video.muted,
    playbackRate: video.playbackRate,
    pictureInPicture: document.pictureInPictureElement === video,
    videoWidth: video.videoWidth,
    videoHeight: video.videoHeight,
    cid: Number.isSafeInteger(Number(window.__INITIAL_STATE__?.cid)) ? Number(window.__INITIAL_STATE__.cid) : null
  };
})()
