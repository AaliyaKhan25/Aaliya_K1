using System.Text.Json;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace ResilientForm;

public sealed record Settings(string Url, string FirstName, string Gender, string? State, bool Headless, int TimeoutSec);
public sealed record Info(string Labels, string Prev, string Ctx, string Attrs, string Opt, string Value,
                          string Name, string Group, List<string> Options);
public sealed record Cand(IWebElement El, Info I);
public sealed record Outcome(string Field, bool Ok, string Detail);

/// <summary>
/// Fills First Name (text), Gender (radio) and State (dropdown) on a form WITHOUT relying on a fixed
/// id / name / class / XPath / field order. Fields are found by *what a human would read*:
/// the associated label, aria-label, placeholder, the text just before the control, the option
/// texts (Male/Female, a list of states), with id/name only as a weak extra hint.
/// Every candidate gets a score; the best one above a threshold wins.
/// </summary>
public static class Program
{
    const string DefaultUrl = "https://app.cloudqa.io/home/AutomationPracticeForm";
    const int MinScore = 40;
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // ---------- semantic vocabularies (applied to normalised text: lowercase, words split) ----------
    static readonly Regex FirstPos = new(@"\b(first ?name|fname|given ?name|forename)\b", RegexOptions.Compiled);
    static readonly Regex FirstNeg = new(@"\b(last|lname|surname|family|middle|full ?name|user ?name|nick ?name|company|father|mother)\b", RegexOptions.Compiled);
    static readonly Regex StatePos = new(@"\b(state|province|region)\b", RegexOptions.Compiled);
    static readonly Regex StateNeg = new(@"\b(country|city|district|county|zip|postal|pin ?code|nationality|town)\b", RegexOptions.Compiled);
    static readonly Regex GenderPos = new(@"\b(gender|sex)\b", RegexOptions.Compiled);
    static readonly Regex Placeholder = new(@"^(select|choose|please|pick|none|any)\b", RegexOptions.Compiled);

    static readonly Dictionary<string, string[]> Synonyms = new()
    {
        ["male"] = new[] { "male", "m", "man", "boy" },
        ["female"] = new[] { "female", "f", "woman", "girl" },
        ["other"] = new[] { "other", "others", "non binary", "nonbinary", "prefer not to say" },
    };

    static readonly HashSet<string> KnownStates = new(
        ("alabama,alaska,arizona,arkansas,california,colorado,connecticut,delaware,florida,georgia,hawaii,idaho,illinois,indiana,iowa,kansas,kentucky,louisiana,maine,maryland,massachusetts,michigan,minnesota,mississippi,missouri,montana,nebraska,nevada,new hampshire,new jersey,new mexico,new york,north carolina,north dakota,ohio,oklahoma,oregon,pennsylvania,rhode island,south carolina,south dakota,tennessee,texas,utah,vermont,virginia,washington,west virginia,wisconsin,wyoming," +
         "andhra pradesh,arunachal pradesh,assam,bihar,chhattisgarh,goa,gujarat,haryana,himachal pradesh,jharkhand,karnataka,kerala,madhya pradesh,maharashtra,manipur,meghalaya,mizoram,nagaland,odisha,punjab,rajasthan,sikkim,tamil nadu,telangana,tripura,uttar pradesh,uttarakhand,west bengal,delhi")
        .Split(','));

    // ---------- JavaScript helpers (run in the page; they also walk open shadow roots) ----------
    const string CollectJs = """
        const sel = arguments[0]; const out = [];
        (function walk(root) {
          root.querySelectorAll(sel).forEach(e => out.push(e));
          root.querySelectorAll('*').forEach(e => { if (e.shadowRoot) walk(e.shadowRoot); });
        })(document);
        return out;
        """;

    const string DescribeJs = """
        const el = arguments[0];
        const clean = t => (t || '').replace(/\s+/g, ' ').trim();
        const root = el.getRootNode();
        const labels = [];
        if (el.labels) for (const l of el.labels) labels.push(clean(l.textContent));
        const lb = el.getAttribute('aria-labelledby');
        if (lb) lb.split(/\s+/).forEach(id => { try { const n = root.querySelector('#' + CSS.escape(id)); if (n) labels.push(clean(n.textContent)); } catch (e) {} });
        ['aria-label', 'placeholder', 'title'].forEach(a => { const v = el.getAttribute(a); if (v) labels.push(clean(v)); });
        const hasField = n => n.nodeType === 1 && (n.matches('input,select,textarea') || n.querySelector('input,select,textarea'));
        const prevText = () => {
          let n = el;
          for (let up = 0; up < 4 && n; up++) {
            let s = n.previousSibling;
            while (s) {
              const okNode = (s.nodeType === 3) || (s.nodeType === 1 && !s.matches('script,style'));
              if (okNode && !hasField(s)) { const t = clean(s.textContent); if (t) return t.slice(-60); }
              s = s.previousSibling;
            }
            n = n.parentElement;
          }
          return '';
        };
        const strip = n => { const c = n.cloneNode(true); c.querySelectorAll('select,option,script,style,textarea').forEach(x => x.remove()); return clean(c.textContent); };
        const ctxText = () => {
          let n = el.parentElement;
          for (let up = 0; up < 4 && n; up++, n = n.parentElement) { const t = strip(n); if (t && t.length <= 80) return t; }
          return '';
        };
        let opt = labels.length ? labels[0] : '';
        if (!opt) { let s = el.nextSibling; while (s && s.nodeType === 3 && !clean(s.textContent)) s = s.nextSibling;
                    if (s && !hasField(s)) opt = clean(s.textContent).slice(0, 40); }
        if (!opt && el.parentElement) { const t = clean(el.parentElement.textContent); if (t.length <= 30) opt = t; }
        let group = [];
        if ((el.type || '') === 'radio') {
          const radios = el.name ? [...root.querySelectorAll('input[type=radio]')].filter(r => r.name === el.name) : [el];
          let n = el.parentElement;
          while (n && !radios.every(r => n.contains(r))) n = n.parentElement;
          for (let up = 0; up < 3 && n; up++, n = n.parentElement) {
            const lg = n.querySelector('legend'); if (lg) group.push(clean(lg.textContent));
            const t = strip(n); if (t.length <= 120) group.push(t);
          }
        }
        const attrs = ['id', 'name', 'autocomplete', 'data-testid', 'data-test', 'data-qa', 'formcontrolname']
                        .map(a => el.getAttribute(a) || '').join(' ');
        return JSON.stringify({
          labels: labels.join(' | '), prev: prevText(), ctx: ctxText(), attrs, opt,
          value: el.value || '', name: el.name || '', group: group.join(' | '),
          options: el.tagName === 'SELECT' ? [...el.options].map(o => clean(o.text)) : []
        });
        """;

    const string SetValueJs = """
        const e = arguments[0];
        const proto = e.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        Object.getOwnPropertyDescriptor(proto, 'value').set.call(e, arguments[1]);
        e.dispatchEvent(new Event('input', { bubbles: true }));
        e.dispatchEvent(new Event('change', { bubbles: true }));
        """;

    const string SelectIndexJs = """
        const s = arguments[0]; s.selectedIndex = arguments[1];
        s.dispatchEvent(new Event('input', { bubbles: true }));
        s.dispatchEvent(new Event('change', { bubbles: true }));
        return s.options[s.selectedIndex].text;
        """;

    const string ClickRadioJs = """
        const e = arguments[0];
        if (e.labels && e.labels.length) e.labels[0].click(); else e.click();
        if (!e.checked) { e.checked = true;
          e.dispatchEvent(new Event('input', { bubbles: true }));
          e.dispatchEvent(new Event('change', { bubbles: true })); }
        return e.checked;
        """;

    // ------------------------------------------------------------------------------------------
    public static int Main(string[] args)
    {
        var cfg = ParseArgs(args);
        Console.WriteLine($"Target URL : {cfg.Url}");

        var opts = new ChromeOptions();
        if (cfg.Headless) opts.AddArgument("--headless=new");
        opts.AddArgument("--window-size=1400,1000");

        using var driver = new ChromeDriver(opts);
        driver.Navigate().GoToUrl(cfg.Url);
        WaitForPage(driver, cfg.TimeoutSec);

        var results = new List<Outcome>
        {
            Run(driver, cfg, "First Name", FillFirstName),
            Run(driver, cfg, "Gender",     PickGender),
            Run(driver, cfg, "State",      PickState),
        };

        try { driver.SwitchTo().DefaultContent(); ((ITakesScreenshot)driver).GetScreenshot().SaveAsFile("result.png"); }
        catch (Exception) { /* screenshot is best-effort */ }

        Console.WriteLine();
        foreach (var r in results)
            Console.WriteLine($"[{(r.Ok ? "PASS" : "FAIL")}] {r.Field,-10} {r.Detail}");

        bool allOk = results.All(r => r.Ok);
        Console.WriteLine(allOk ? "\nRESULT: PASS" : "\nRESULT: FAIL");
        return allOk ? 0 : 1;
    }

    static Settings ParseArgs(string[] a)
    {
        string? Get(string key) { for (int i = 0; i < a.Length - 1; i++) if (a[i] == key) return a[i + 1]; return null; }
        string? positional = a.FirstOrDefault(x => x.StartsWith("http") || x.StartsWith("file:"));
        var url = Get("--url") ?? positional ?? Environment.GetEnvironmentVariable("FORM_URL") ?? DefaultUrl;
        var timeout = int.TryParse(Get("--timeout"), out var t) ? t : 15;
        return new Settings(
            url,
            Get("--first-name") ?? "Aaliya",
            Get("--gender") ?? "Female",
            Get("--state"),                    // null => first real option
            a.Contains("--headless"),
            timeout);
    }

    static void WaitForPage(IWebDriver d, int timeoutSec)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < end)
        {
            try
            {
                var ready = (string?)Js(d, "return document.readyState") == "complete";
                if (ready && Elements(d, "input,select").Any()) return;
                if (ready && d.FindElements(By.CssSelector("iframe,frame")).Count > 0) return;
            }
            catch (WebDriverException) { }
            Thread.Sleep(300);
        }
    }

    // ---------- retry + iframe handling ----------
    static Outcome Run(IWebDriver d, Settings c, string field, Func<IWebDriver, Settings, Outcome?> attempt)
    {
        var deadline = DateTime.UtcNow.AddSeconds(c.TimeoutSec);
        string last = "no element on the page matched this field well enough";
        do
        {
            foreach (var _ in Frames(d))
            {
                try { var r = attempt(d, c); if (r != null) return r; }   // stays inside the frame it was found in
                catch (Exception ex) { last = ex.Message.Split('\n')[0]; }
            }
            Thread.Sleep(500);                                           // page may still be rendering
        } while (DateTime.UtcNow < deadline);

        d.SwitchTo().DefaultContent();
        return new Outcome(field, false, last);
    }

    static IEnumerable<int> Frames(IWebDriver d)
    {
        d.SwitchTo().DefaultContent();
        yield return -1;
        int n = d.FindElements(By.CssSelector("iframe,frame")).Count;
        for (int i = 0; i < n; i++)
        {
            d.SwitchTo().DefaultContent();
            bool ok = false;
            try { d.SwitchTo().Frame(i); ok = true; } catch (WebDriverException) { }
            if (ok) yield return i;
        }
        d.SwitchTo().DefaultContent();
    }

    // ---------- the three fields ----------
    static Outcome? FillFirstName(IWebDriver d, Settings c)
    {
        var inputs = Collect(d, "input,textarea").Where(x => IsTextual(x.El) && Usable(x.El, needDisplayed: true));
        var (best, why) = PickInfo(inputs, i => ScoreText(i, FirstPos, FirstNeg));
        if (best == null) return null;

        var el = best.El;
        Scroll(d, el);
        try { el.Clear(); el.SendKeys(c.FirstName); } catch (WebDriverException) { }
        if (el.GetAttribute("value") != c.FirstName) Js(d, SetValueJs, el, c.FirstName);   // fallback for odd widgets

        bool ok = el.GetAttribute("value") == c.FirstName;
        return new Outcome("First Name", ok, ok ? $"typed '{c.FirstName}' ({why})" : $"value did not stick ({why})");
    }

    static Outcome? PickGender(IWebDriver d, Settings c)
    {
        var radios = Collect(d, "input[type=radio]");
        if (radios.Count == 0) return null;

        // radios that share a name are one group (that is HTML's own definition, so it survives renames)
        var groups = radios.Select((r, i) => (R: r, Key: string.IsNullOrEmpty(r.I.Name) ? $"#{i}" : r.I.Name))
                           .GroupBy(x => x.Key).Select(g => g.Select(x => x.R).ToList()).ToList();

        var ranked = groups.Select(g => (G: g, S: ScoreGender(g))).OrderByDescending(x => x.S).ToList();
        if (ranked[0].S < MinScore) return null;
        var group = ranked[0].G;

        var want = Norm(c.Gender);
        var target = group.FirstOrDefault(r => OptionMatches(r.I, want));
        if (target == null)
            return new Outcome("Gender", false,
                $"group found but no option '{c.Gender}'; options: {string.Join(", ", group.Select(r => r.I.Opt))}");

        Scroll(d, target.El);
        try { if (target.El.Displayed && target.El.Enabled) target.El.Click(); } catch (WebDriverException) { }
        if (!target.El.Selected) Js(d, ClickRadioJs, target.El);        // custom-styled/hidden radios

        bool ok = target.El.Selected;
        var others = group.Where(r => r != target && r.El.Selected).ToList();
        ok &= others.Count == 0;
        return new Outcome("Gender", ok, ok
            ? $"selected '{target.I.Opt}' from group [{string.Join("/", group.Select(r => r.I.Opt))}]"
            : "radio did not end up selected");
    }

    static Outcome? PickState(IWebDriver d, Settings c)
    {
        var selects = Collect(d, "select").Where(x => Usable(x.El, needDisplayed: false));
        var (best, why) = Pick(selects, ScoreState);
        if (best == null) return PickStateCustom(d, c);                // not a native <select>

        var opts = best.I.Options;
        int idx;
        if (!string.IsNullOrWhiteSpace(c.State))
        {
            var want = Norm(c.State);
            idx = opts.FindIndex(o => Norm(o) == want);
            if (idx < 0) idx = opts.FindIndex(o => Norm(o).Contains(want));
            if (idx < 0)
                return new Outcome("State", false, $"dropdown found but no option '{c.State}'; options: {string.Join(", ", opts.Take(15))}…");
        }
        else
        {
            idx = opts.FindIndex(o => { var n = Norm(o); return n.Length > 0 && !Placeholder.IsMatch(n); });
            if (idx < 0) return new Outcome("State", false, "dropdown has no selectable options");
        }

        Scroll(d, best.El);
        var chosen = (string?)Js(d, SelectIndexJs, best.El, idx);
        bool ok = chosen == opts[idx];
        return new Outcome("State", ok, ok ? $"selected '{chosen}' ({why})" : "selection did not apply");
    }

    /// Fallback for custom dropdowns (role=combobox / aria-haspopup) that are not a native <select>.
    static Outcome? PickStateCustom(IWebDriver d, Settings c)
    {
        var boxes = Collect(d, "[role=combobox],[aria-haspopup=listbox],[aria-haspopup=true]")
                    .Where(x => Usable(x.El, needDisplayed: true));
        var (best, why) = PickInfo(boxes, i => ScoreText(i, StatePos, StateNeg));
        if (best == null) return null;

        Scroll(d, best.El);
        best.El.Click();
        Thread.Sleep(400);
        var options = Elements(d, "[role=option],li").Where(e => Usable(e, true))
                      .Select(e => (E: e, T: Norm(e.Text))).Where(x => x.T.Length > 0).ToList();
        var want = Norm(c.State);
        var pick = string.IsNullOrEmpty(want)
            ? options.FirstOrDefault(x => !Placeholder.IsMatch(x.T))
            : options.FirstOrDefault(x => x.T == want);
        if (pick.E == null) return new Outcome("State", false, "custom dropdown opened but no matching option");
        pick.E.Click();
        return new Outcome("State", true, $"custom dropdown: selected '{pick.E.Text}' ({why})");
    }

    // ---------- scoring ----------
    static int ScoreText(Info i, Regex pos, Regex neg)
    {
        string l = Norm(i.Labels), p = Norm(i.Prev), a = Norm(i.Attrs), c = Norm(i.Ctx);
        int s = 0;
        bool human = pos.IsMatch(l) || pos.IsMatch(p);
        if (pos.IsMatch(l)) s += 100;     // <label for>, wrapping label, aria-label, placeholder
        if (pos.IsMatch(p)) s += 70;      // text right before the control (works for table / div layouts)
        if (pos.IsMatch(a)) s += 50;      // id/name hint - weak, may be renamed
        if (pos.IsMatch(c)) s += 25;      // nearby container text
        if (neg.IsMatch(l)) s -= 200;     // "Last Name", "Full Name" ... look-alikes
        if (neg.IsMatch(p)) s -= 120;
        if (!human && neg.IsMatch(a)) s -= 200;
        return s;
    }

    static int ScoreState(Cand c)
    {
        int s = ScoreText(c.I, StatePos, StateNeg);
        if (c.I.Options.Count(o => KnownStates.Contains(Norm(o))) >= 5) s += 60;   // content looks like a list of states
        try { if (c.El.Displayed) s += 10; } catch (WebDriverException) { }
        return s;
    }

    static int ScoreGender(List<Cand> group)
    {
        var opts = group.Select(r => Norm(r.I.Opt)).ToHashSet();
        int s = 0;
        if (opts.Contains("male") && opts.Contains("female")) s += 100;
        else if (opts.Overlaps(Synonyms["male"]) || opts.Overlaps(Synonyms["female"])) s += 30;
        var first = group[0].I;
        if (GenderPos.IsMatch(Norm(first.Group)) || GenderPos.IsMatch(Norm(first.Attrs))) s += 80;
        return s;
    }

    static bool OptionMatches(Info i, string want)
    {
        var alts = Synonyms.TryGetValue(want, out var a) ? a : new[] { want };
        return alts.Contains(Norm(i.Opt)) || alts.Contains(Norm(i.Value));
    }

    // ---------- small utilities ----------
    static (Cand? Best, string Why) Pick(IEnumerable<Cand> cands, Func<Cand, int> score) =>
        PickCore(cands.Select(c => (C: c, S: score(c))).OrderByDescending(x => x.S).ToList());

    static (Cand? Best, string Why) PickInfo(IEnumerable<Cand> cands, Func<Info, int> score) =>
        Pick(cands, c => score(c.I));

    static (Cand? Best, string Why) PickCore(List<(Cand C, int S)> ranked)
    {
        if (ranked.Count == 0 || ranked[0].S < MinScore) return (null, "");
        var b = ranked[0];
        var text = b.C.I.Labels.Length > 0 ? b.C.I.Labels : b.C.I.Prev;
        return (b.C, $"matched on '{(text.Length > 40 ? text[..40] : text)}', score {b.S}");
    }

    static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = Regex.Replace(s, "([a-z0-9])([A-Z])", "$1 $2");               // firstName -> first Name
        return Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
    }

    static object? Js(IWebDriver d, string script, params object[] args) =>
        ((IJavaScriptExecutor)d).ExecuteScript(script, args);

    static List<IWebElement> Elements(IWebDriver d, string css) =>
        ((IEnumerable<object>)(Js(d, CollectJs, css) ?? new List<object>())).Cast<IWebElement>().ToList();

    static List<Cand> Collect(IWebDriver d, string css)
    {
        var res = new List<Cand>();
        foreach (var el in Elements(d, css))
        {
            try
            {
                var json = (string?)Js(d, DescribeJs, el);
                if (json != null) res.Add(new Cand(el, JsonSerializer.Deserialize<Info>(json, Json)!));
            }
            catch (WebDriverException) { }   // element vanished mid-scan
        }
        return res;
    }

    static bool Usable(IWebElement e, bool needDisplayed)
    {
        try { return e.Enabled && (!needDisplayed || e.Displayed); } catch (WebDriverException) { return false; }
    }

    static bool IsTextual(IWebElement e)
    {
        var t = (e.GetAttribute("type") ?? "text").ToLowerInvariant();
        return t is "text" or "search" or "";
    }

    static void Scroll(IWebDriver d, IWebElement e)
    {
        try { Js(d, "arguments[0].scrollIntoView({block:'center'})", e); } catch (WebDriverException) { }
    }
}