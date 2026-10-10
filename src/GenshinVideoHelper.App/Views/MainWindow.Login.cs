using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.App;

public partial class MainWindow
{
    private LoginPromptWindow? _loginPrompt;
    private long _loginIntent;
    private bool OwnsLoginRequest(FollowRequest request) => !_closing && Services.Follow.Session.IsCurrent(request);

    private void DismissStaleLoginPrompt(FollowSnapshot snapshot)
    {
        if (_loginPrompt?.Tag is long version && snapshot.Request?.Version != version)
            _loginPrompt.Close();
    }

    // Fire-and-forget UI work has its own error boundary; playback never waits for account checks.
    private async Task CheckLoginAsync(FollowRequest request)
    {
        if (Services.VideoAccount is not { } account || Services.Follow.Current.Page is not { } page) return;
        try
        {
            BilibiliAccountState? state = null;
            for (var attempt = 0; attempt < 5 && OwnsLoginRequest(request); attempt++)
            {
                state = await account.ReadAccountAsync(page, request.Token);
                if (!OwnsLoginRequest(request)) return;
                if (VideoIdentity.TryParse(state.Url, out var identity) && identity == request.Identity && state.LoggedIn is not null) break;
                state = null;
                await Task.Delay(1000, request.Token);
            }
            if (!OwnsLoginRequest(request) || state is null) return;
            if (state.LoggedIn == true) { await TryHighQualityAsync(request, page, account); return; }
            _loginPrompt?.Close();
            var prompt = new LoginPromptWindow { Owner = this, Tag = request.Version };
            _loginPrompt = prompt;
            prompt.Closed += (_, _) => { if (_loginPrompt == prompt) _loginPrompt = null; };
            prompt.LoginRequested += () => { if (OwnsLoginRequest(request)) _ = LoginInBrowserAsync(request, page, account); };
            AppLog.Info("Account", "匿名会话：提示登录以解锁高清。");
            prompt.Show();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Account", "登录状态暂不可用，继续原播放流程。", ex); }
    }

    private async Task LoginInBrowserAsync(FollowRequest request, BrowserPage page, IBilibiliAccountService account)
    {
        try
        {
            if (Services.Browser is not IBrowserWindow window || !OwnsLoginRequest(request)) return;
            await window.ShowAsync(page, request.Token);
            if (!OwnsLoginRequest(request)) return;
            SetStatus("请在助手的 Chrome 中登录 B 站，完成后将自动检查高清画质。");
            // Bounded monitoring only after an explicit login choice, never on every playback poll.
            for (var attempt = 0; attempt < 90 && OwnsLoginRequest(request); attempt++)
            {
                await Task.Delay(2000, request.Token);
                var state = await account.ReadAccountAsync(page, request.Token);
                if (!OwnsLoginRequest(request)) return;
                if (!VideoIdentity.TryParse(state.Url, out var identity)) continue;
                if (identity != request.Identity) return;
                if (state.LoggedIn != true) continue;
                AppLog.Info("Account", "专用 Chrome 登录状态已确认。");
                await TryHighQualityAsync(request, page, account);
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("Account", "登录或高清检查未完成。", ex);
            if (OwnsLoginRequest(request)) SetStatus("登录或高清检查暂未完成，可以在 Chrome 中手动选择画质。", true);
        }
    }

    private async Task TryHighQualityAsync(FollowRequest request, BrowserPage page, IBilibiliAccountService account)
    {
        try
        {
            if (!OwnsLoginRequest(request)) return;
            await account.RequestHighQualityAsync(page, request.Token);
            for (var attempt = 0; attempt < 6 && OwnsLoginRequest(request); attempt++)
            {
                await Task.Delay(1000, request.Token);
                var state = await account.ReadAccountAsync(page, request.Token);
                if (!OwnsLoginRequest(request)) return;
                if (!VideoIdentity.TryParse(state.Url, out var identity) || identity != request.Identity) return;
                if (state.LoggedIn != true) return;
                if (state.VideoHeight < 1080 && attempt < 5) continue;
                AppLog.Info("Account", $"登录后实际视频解码尺寸：{state.VideoWidth}×{state.VideoHeight}。");
                SetStatus(state.VideoHeight >= 1080
                    ? $"已登录，实际视频画质 {state.VideoWidth}×{state.VideoHeight}。"
                    : $"已登录，当前画质 {state.VideoWidth}×{state.VideoHeight}，可在 Chrome 中手动选择高清。");
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("Account", "自动高清请求未完成。", ex);
            if (OwnsLoginRequest(request)) SetStatus("已登录，可在 Chrome 中手动选择高清画质。");
        }
    }
}
