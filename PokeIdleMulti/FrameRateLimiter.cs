namespace PokeIdleMulti;

/// <summary>
/// Limits visual requestAnimationFrame callbacks in the top-level PokeIdle page.
/// Does not change network, timers, the server simulation or separate Twitch/Kick windows.
/// Affects other rAF-driven animations on the same game page, not exclusively the map.
/// </summary>
internal static class FrameRateLimiter
{
    public static readonly int[] Values = [15, 30, 45, 60, 0];

    public static int Normalize(int fps) => fps is 0 or 15 or 30 or 45 or 60 ? fps : 15;

    public static string BootScript(int fps) => Script +
        "\nwindow.__nightDeckFrameControl?.setFps(" + Normalize(fps) + ");";

    public static string SetScript(int fps) =>
        "window.__nightDeckFrameControl?.setFps(" + Normalize(fps) + ") ?? false";

    private const string Script = """
        (() => {
          if (window.top !== window || !['pokeidle.io', 'deck.test'].includes(location.hostname) || window.__nightDeckFrameControl)
            return;
          // Rate limit visual frame callbacks only on the game's top-level page; never touch live windows.
          const nativeFrame = window.requestAnimationFrame.bind(window);
          const nativeCancel = window.cancelAnimationFrame.bind(window);
          const pending = new Map();
          let nextId = 0;
          let ticket = 0;
          let nextDue = 0;
          let limit = 15;
          const allowed = [0, 15, 30, 45, 60];
          const tick = now => {
            ticket = 0;
            if (!pending.size) return;
            const interval = limit ? 1000 / limit : 0;
            if (interval && nextDue && now + 0.5 < nextDue) {
              ticket = nativeFrame(tick);
              return;
            }
            if (interval)
              nextDue = !nextDue || now - nextDue > interval * 2 ? now + interval : nextDue + interval;
            const frameCallbacks = [...pending.values()];
            pending.clear();
            for (const callback of frameCallbacks) {
              try { callback(now); } catch (error) { setTimeout(() => { throw error; }, 0); }
            }
            if (pending.size) ticket = nativeFrame(tick);
          };
          window.requestAnimationFrame = callback => {
            if (limit === 0 || typeof callback !== 'function') return nativeFrame(callback);
            const id = --nextId;
            pending.set(id, callback);
            if (!ticket) ticket = nativeFrame(tick);
            return id;
          };
          window.cancelAnimationFrame = id => {
            if (typeof id === 'number' && id < 0) {
              pending.delete(id);
              if (!pending.size && ticket) { nativeCancel(ticket); ticket = 0; }
            } else nativeCancel(id);
          };
          window.__nightDeckFrameControl = Object.freeze({
            setFps(value) {
              const fps = Number(value);
              if (!allowed.includes(fps)) return false;
              limit = fps;
              nextDue = 0;
              return true;
            },
            getFps() { return limit; }
          });
        })();
        """;
}
