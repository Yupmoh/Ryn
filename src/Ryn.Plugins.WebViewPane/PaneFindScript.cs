using System.Text.Json;

namespace Ryn.Plugins.WebViewPane;

/// <summary>
/// Builds the injected find-in-page engine. The engine runs identically on all three webview engines:
/// it walks text nodes for matches and paints them with the CSS Custom Highlight API — no DOM mutation,
/// no reliance on engine-specific native find APIs. On engines without CSS.highlights (WebKit before
/// Safari 17.2) match counting, cycling, and scroll-to-match still work; only the visual highlight is
/// absent. State lives in the page, so a navigation clears the session (re-issue find after load).
/// </summary>
internal static class PaneFindScript
{
    /// <summary>
    /// The engine, installed once per document as <c>window.__rynFind</c>. Kept to same-text-node
    /// matches — a match spanning element boundaries (e.g. across a <c>&lt;b&gt;</c> split) is not found,
    /// which matches the behavior of simple finders. Hidden elements are filtered via getClientRects.
    /// </summary>
    private const string Engine =
        """
        window.__rynFind = (() => {
          const NAME = 'ryn-find', ACTIVE = 'ryn-find-active';
          let matches = [], active = -1, styleEl = null;
          const supported = typeof Highlight !== 'undefined' && !!CSS.highlights;
          const ensureStyle = () => {
            if (styleEl || !supported) return;
            styleEl = document.createElement('style');
            styleEl.textContent =
              '::highlight(ryn-find){background-color:#ffdd57;color:#000}' +
              '::highlight(ryn-find-active){background-color:#ff8c1a;color:#000}';
            document.documentElement.appendChild(styleEl);
          };
          const collect = (text, matchCase) => {
            const ranges = [];
            const needle = matchCase ? text : text.toLowerCase();
            const root = document.body || document.documentElement;
            if (!root) return ranges;
            const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
              acceptNode(node) {
                const p = node.parentElement;
                if (!p) return NodeFilter.FILTER_REJECT;
                const tag = p.tagName;
                if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'TEMPLATE')
                  return NodeFilter.FILTER_REJECT;
                if (p.getClientRects().length === 0)
                  return NodeFilter.FILTER_REJECT;
                return NodeFilter.FILTER_ACCEPT;
              }
            });
            let node;
            while ((node = walker.nextNode())) {
              const hay = matchCase ? node.data : node.data.toLowerCase();
              let i = 0;
              while ((i = hay.indexOf(needle, i)) !== -1) {
                const r = new Range();
                r.setStart(node, i);
                r.setEnd(node, i + text.length);
                ranges.push(r);
                i += text.length;
              }
            }
            return ranges;
          };
          const paint = () => {
            if (!supported) return;
            CSS.highlights.set(NAME, new Highlight(...matches));
            if (active >= 0) CSS.highlights.set(ACTIVE, new Highlight(matches[active]));
            else CSS.highlights.delete(ACTIVE);
          };
          const reveal = () => {
            if (active < 0) return;
            const el = matches[active].startContainer.parentElement;
            if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center', inline: 'nearest' });
          };
          const result = () => ({ matches: matches.length, activeIndex: active });
          return {
            find(text, forward, matchCase) {
              this.stop(true);
              if (!text) return result();
              matches = collect(String(text), !!matchCase);
              active = matches.length === 0 ? -1 : (forward === false ? matches.length - 1 : 0);
              ensureStyle();
              paint();
              reveal();
              return result();
            },
            next(forward) {
              if (matches.length === 0) return result();
              active = forward === false
                ? (active - 1 + matches.length) % matches.length
                : (active + 1) % matches.length;
              paint();
              reveal();
              return result();
            },
            stop(clearHighlights) {
              if (clearHighlights !== false && supported) {
                CSS.highlights.delete(NAME);
                CSS.highlights.delete(ACTIVE);
              }
              matches = [];
              active = -1;
              return { matches: 0, activeIndex: -1 };
            }
          };
        })();
        """;

    internal static string BuildFind(string text, bool forward, bool matchCase)
    {
        var textLiteral = JsonSerializer.Serialize(text, WebViewPaneJsonContext.Default.String);
        return
            $$"""
            (() => {
              if (!window.__rynFind) {
            {{Engine}}
              }
              return window.__rynFind.find({{textLiteral}}, {{Js(forward)}}, {{Js(matchCase)}});
            })()
            """;
    }

    internal static string BuildNext(bool forward) =>
        $"(() => window.__rynFind ? window.__rynFind.next({Js(forward)}) : ({{ matches: 0, activeIndex: -1 }}))()";

    internal static string BuildStop(bool clearHighlights) =>
        $"(() => window.__rynFind ? window.__rynFind.stop({Js(clearHighlights)}) : ({{ matches: 0, activeIndex: -1 }}))()";

    private static string Js(bool value) => value ? "true" : "false";
}
