# Aaliya_K1
# Resilient Form Automation (C# + Selenium)

A C# / Selenium program that fills three fields of the CloudQA Automation Practice Form - **First Name** (text box), **Gender** (radio button) and **State** (dropdown) - and keeps working when the page changes.

> Author: Aaliya Khan
> Target page: https://app.cloudqa.io/home/AutomationPracticeForm (the URL is configurable)

---

## Table of contents

1. [The problem](#1-the-problem)
2. [The idea in one paragraph](#2-the-idea-in-one-paragraph)
3. [Features](#3-features)
4. [Requirements](#4-requirements)
5. [Setup and run](#5-setup-and-run)
6. [Command-line options](#6-command-line-options)
7. [Example output](#7-example-output)
8. [How it works](#8-how-it-works)
9. [Scoring tables](#9-scoring-tables)
10. [How each field is filled and verified](#10-how-each-field-is-filled-and-verified)
11. [Why this approach and not the alternatives](#11-why-this-approach-and-not-the-alternatives)
12. [Testing against modified pages](#12-testing-against-modified-pages)
13. [Project structure](#13-project-structure)
14. [Known limitations](#14-known-limitations)
15. [Troubleshooting](#15-troubleshooting)

---

## 1. The problem

Write an automated test for three form fields, with this key requirement: **the program must keep working if the page changes.** For example:

- the fields move into a different order,
- their `id`, `name` or `class` attributes change,
- the HTML structure changes slightly,
- other fields on the page look similar (Last Name, Full Name, Country ...).

A fixed XPath or CSS selector (`By.Id("firstName")`, `//div[3]/input`) breaks in all of these situations, so it is not used here. The page URL must also be configurable.

## 2. The idea in one paragraph

A person finds the "First Name" box by **reading**, not by looking at its `id`. This program does the same. For every candidate element on the page it collects what a human would read - the label, `aria-label`, placeholder, the text just before the control, the option texts - and gives it a **score**. The best candidate above a minimum score wins. The `id` and `name` attributes are used only as a weak extra hint, so renaming them cannot break the test. Look-alike fields such as "Last Name" or "Country" are given negative scores so they can never win.

## 3. Features

- **No fixed XPath/CSS selectors anywhere.** Elements are found by meaning.
- **Survives page changes:** reordered fields, renamed `id`/`name`/`class`, changed nesting, extra look-alike fields.
- **Handles hard cases:** iframes, open Shadow DOM, hidden or custom-styled radio buttons, and custom (non-`<select>`) dropdowns.
- **Repeated blocks:** the practice page repeats the Gender radio block several times (main form and Shadow DOM sections). The program splits them into separate groups and prefers the visible one.
- **Waits and retries:** if the page is still rendering, the search is repeated until the timeout.
- **Verifies its own work:** after each action it checks that the value really ended up in the field.
- **Configurable:** URL, values and timeout come from command-line arguments or an environment variable.
- **CI-friendly:** exit code `0` for pass, `1` for fail; optional headless mode; saves a screenshot.

## 4. Requirements

| Item | Notes |
|---|---|
| .NET SDK 8.0 or newer | Check with `dotnet --version` |
| Google Chrome | Any recent version |
| Internet access on the first run | Downloads the Selenium NuGet package and chromedriver (Selenium Manager does this automatically) |

No other tools are needed, and you do not have to download chromedriver by hand.

## 5. Setup and run

```bash
git clone <this-repository-url>
cd <repository-folder>
dotnet run -- --url https://app.cloudqa.io/home/AutomationPracticeForm
```

Note the `--` after `dotnet run`: everything after it is passed to the program, not to `dotnet`.

If your SDK is newer than 8 (for example `dotnet --version` prints `10.0.x`), open `ResilientForm.csproj` and change `net8.0` to the matching version (`net10.0`).

Choose your own values:

```bash
dotnet run -- --url https://app.cloudqa.io/home/AutomationPracticeForm --first-name Priya --gender Male --state India
```

Write values **without commas**, and use quotes only if a value contains spaces (`--first-name "Mary Ann"`).

Run without showing the browser window:

```bash
dotnet run -- --url https://app.cloudqa.io/home/AutomationPracticeForm --headless
```

## 6. Command-line options

| Option | Default | Meaning |
|---|---|---|
| `--url <address>` | CloudQA practice form | Page to test. Also accepted: the first `http...` or `file:...` argument, or the `FORM_URL` environment variable. Order of priority: `--url`, bare URL argument, `FORM_URL`, default. |
| `--first-name <text>` | `Aaliya` | Text typed into the First Name box |
| `--gender <value>` | `Female` | Option to select, matched against the option's label text, e.g. `Male`, `Female`, `Transgender` (synonyms such as `m`, `f` also work) |
| `--state <text>` | *(first real option)* | Option to select in the State dropdown. If omitted, the first non-placeholder option is chosen |
| `--headless` | off | Run Chrome without a window |
| `--timeout <seconds>` | `15` | How long to keep searching for each field |

Environment variable example (PowerShell):

```powershell
$env:FORM_URL = "file:///E:/pages/modified-form.html"
dotnet run
```

**Exit codes:** `0` = all three fields passed, `1` = at least one failed. A screenshot is saved as `result.png` in the working folder.

## 7. Example output

```
Target URL : https://app.cloudqa.io/home/AutomationPracticeForm

[PASS] First Name typed 'Priya' (matched on 'First Name | Name', score 245)
[PASS] Gender     selected 'Male' from group [Male/Female/Transgender]
[PASS] State      selected 'India' (matched on 'State', score ...)

RESULT: PASS
```

The text in brackets explains **why** an element was chosen, which makes the result easy to debug.

> **Page quirk:** on the live CloudQA page the dropdown labelled "State" actually lists *countries* (its first option is "-- Select Country --"). The program still finds it correctly because it is the field labelled State. Use a value that exists in it (for example `--state India`), or omit `--state`.

## 8. How it works

```
 open URL ──► wait until page is ready
                 │
                 ▼
   for each field (First Name, Gender, State):
      for each context: main page, then every iframe
         1. collect candidate elements (also inside open shadow roots)
         2. describe each one with JavaScript (label, placeholder, text before it, options ...)
         3. score every candidate
         4. best score >= 40 ?  yes ─► act (type / click / select) and verify
                                no  ─► try the next context; retry until timeout
                 │
                 ▼
   print PASS/FAIL per field, save screenshot, exit code 0 or 1
```

Details:

1. **Collect.** A JavaScript helper walks the page, including open shadow roots, and returns every `input`, `select`, `textarea` or radio.
2. **Describe.** A second helper returns, for each element: associated `<label>` text, `aria-label`, `aria-labelledby`, placeholder, title, the closest text before the element, the text of its container, selected attributes (`id`, `name`, `autocomplete`, `data-testid` ...), and for `<select>` all option texts. For radios it also returns the text of the whole group.
3. **Normalise.** Text is lower-cased and split into words, so `firstName`, `first_name` and `First Name` are all the same to the scorer.
4. **Score.** See the next section.
5. **Act and verify.** See section 10.

## 9. Scoring tables

A candidate must reach a score of at least **40** to be accepted.

### First Name (and the generic text scoring also used for custom dropdowns)

| Signal | Points |
|---|---|
| Label / aria-label / placeholder matches `first name`, `fname`, `given name`, `forename` | +100 |
| Text immediately before the control matches | +70 |
| `id` / `name` / `autocomplete` matches (weak hint) | +50 |
| Text of the surrounding container matches | +25 |
| Label contains a look-alike word: `last`, `surname`, `family`, `middle`, `full name`, `user name`, `nick name`, `company`, `father`, `mother` | -200 |
| Text before the control contains such a word | -120 |
| `id`/`name` contains such a word (only applied when no readable label matched) | -200 |

The attribute penalty is skipped when a human-readable label already matched, so what the user sees always beats a misleading attribute.

### State

Same text scoring with the words `state`, `province`, `region`; look-alike penalties for `country`, `city`, `district`, `county`, `zip`, `postal`, `pin code`, `nationality`, `town`. Additionally:

| Signal | Points |
|---|---|
| At least 5 options are known state names (US and Indian states) | +60 |
| The dropdown is visible | +10 |

### Gender

| Signal | Points |
|---|---|
| The radio group offers both Male and Female | +100 |
| Only some gender synonyms present | +30 |
| Group heading, container text or `name`/`id` contains `gender` or `sex` | +80 |
| The group is visible | +20 |

Radios that share the same `name` form one group (this is how HTML itself defines a radio group). If the same block is repeated with the same `name`, a new group starts whenever an option label repeats.

## 10. How each field is filled and verified

| Field | Action | Fallback | Verification |
|---|---|---|---|
| First Name | Scroll into view, clear, type with `SendKeys` | Sets the value through the browser's native setter and fires `input` and `change` events (works for React/Angular-style inputs) | The element's `value` equals the text |
| Gender | Click the radio that matches the requested option (by its label text or `value`, with synonyms) | Click its `<label>`, then set `checked` and fire events (for hidden or styled radios) | The radio is selected and no other radio in the group is |
| State | Choose the option by position and fire `input` and `change` events | For custom dropdowns (`role=combobox`, `aria-haspopup`): open it and click the matching visible option | The selected option text equals the requested one |

If the requested option does not exist, the program fails with a clear message that lists the available options instead of silently choosing something else.

## 11. Why this approach and not the alternatives

| Alternative | Problem for this task |
|---|---|
| Fixed `id` / CSS / XPath | Breaks as soon as attributes or structure change. Explicitly not allowed |
| XPath based on label text (`//label[text()='First Name']/following::input[1]`) | Assumes the label is a `<label>` placed right before the input; fails with placeholders, `aria-label`, or a changed structure, and can match "Last Name" by accident |
| Selenium relative locators | Still need a fixed anchor and a stable layout |
| Self-healing tools | Need a baseline run on the original page; the modified pages are unknown in advance |
| An AI model reading the page | Slow, costs money, non-deterministic, needs a network connection and key |
| **Scoring on meaning (this project)** | Several independent signals, so losing one does not break it; look-alikes are penalised; deterministic, free and fast |

## 12. Testing against modified pages

The page URL is configurable, so you can test your own modified versions, including local files:

1. Save the form page as an HTML file (browser: *Save page as*) and open it in an editor.
2. Make changes that the task describes and run the program against each one:
   - change or remove every `id`, `name` and `class` of the three fields,
   - reorder the fields,
   - wrap the fields in extra `<div>` / `<span>` elements,
   - turn the label into plain text next to the input (no `<label>` tag),
   - add a decoy field such as "Last Name", "Full Name" or "Country",
   - move the form into an `<iframe>` or a Shadow DOM.
3. Run:

```bash
dotnet run -- --url file:///E:/pages/modified-form.html
```

A good result is `RESULT: PASS` after every change.

## 13. Project structure

```
.
├── Program.cs              # all logic: settings, JS helpers, scoring, field handlers
├── ResilientForm.csproj    # .NET 8 console app, depends on Selenium.WebDriver 4.25.0
├── README.md
└── .gitignore
```

Inside `Program.cs`:

| Part | Purpose |
|---|---|
| `Settings`, `Info`, `Cand`, `Outcome` | Small data records: configuration, a described element, a scored candidate, a field result |
| `CollectJs`, `DescribeJs`, `SetValueJs`, `SelectIndexJs`, `ClickRadioJs` | JavaScript run inside the page |
| `Main`, `ParseArgs`, `WaitForPage` | Entry point, argument parsing, readiness check |
| `Run`, `Frames` | Retry loop and iframe traversal |
| `FillFirstName`, `PickGender`, `PickState`, `PickStateCustom` | The three field handlers |
| `ScoreText`, `ScoreState`, `ScoreGender` | Scoring |
| `Norm`, `Pick`, `Collect`, `Scroll` ... | Helpers |

## 14. Known limitations

- **Visible text is the main signal.** A field with no label, placeholder, aria-label or nearby text and an unreadable `id` cannot be recognised.
- **Languages:** the vocabulary is English. Labels in another language would need extra words.
- **Iframes:** one level of iframes is searched; iframes nested inside iframes are not.
- **Shadow DOM:** only *open* shadow roots can be read. Closed ones are invisible to scripts.
- **Radios** must be real `<input type="radio">` elements. Radio-like widgets made from plain `<div>`s are not supported.
- **Dropdowns:** native `<select>` and common custom dropdowns (`role=combobox`, `aria-haspopup`) are supported; unusual widgets may not be.
- **Ties** are broken by DOM order (first one wins).

## 15. Troubleshooting

| Symptom | Fix |
|---|---|
| `dotnet` is not recognised | Install the .NET SDK, then close and reopen the terminal |
| "You must install or update .NET" / version error | Change `net8.0` in `ResilientForm.csproj` to your SDK's major version |
| `No project was found` | You are in the wrong folder; `ResilientForm.csproj` must be in the current folder |
| First run is slow | Selenium and chromedriver are being downloaded; this happens once |
| Chrome opens and closes immediately, driver error | Update Chrome; check that your network allows the driver download |
| `[FAIL] State ... no option 'X'` | The value is not in that dropdown (on the live page it lists countries). Use an existing option or omit `--state` |
| The typed value contains `"` or `,` | Remove commas and extra quotes from the command-line values |
| `[FAIL] ... no element on the page matched this field well enough` | The page has no readable hint for that field, or it is in an unsupported container (see limitations) |

---

*Built for the CloudQA internship screening task.*