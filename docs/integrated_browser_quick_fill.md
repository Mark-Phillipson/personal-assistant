**Purpose**: Quick, Playwright-free way to autofill the app's form in VS Code's Integrated Browser for manual testing.

**Lessons learned (this session)**
- The Integrated Browser can be scripted via its DevTools Console; no CDP attach required for quick DOM edits.
- Blazor SignalR negotiation failures (net::ERR_CONNECTION_REFUSED) won’t stop DOM-level fills but may prevent server-side saves/real-time updates.
- For repeatable automation and CI, use Playwright/CDP later — the current approach is for fast local testing only.
- Always avoid auto-clicking submit when testing; prefer read-back verification first.

**Quick instructions**
1. Open the form page in the Integrated Browser: `http://localhost:5000/customintellisense`.
2. Open DevTools (right-click → Inspect or use the browser control in the window).
3. Copy the snippet below and paste it into the Console, then press Enter. The script fills fields but does NOT submit the form.

**Autofill script (copy/paste into Console)**

```javascript
(function quickFill(){
  function setByPlaceholder(ph, val){
    const el = document.querySelector(`[placeholder="${ph}"]`);
    if(!el) return false;
    el.focus();
    el.value = val;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    return true;
  }

  function selectByLabelText(labelText, optionText){
    // find select near a label containing labelText
    const labels = Array.from(document.querySelectorAll('label'));
    const label = labels.find(l=> l.textContent && l.textContent.trim().includes(labelText));
    let sel = null;
    if(label){
      const forId = label.getAttribute('for');
      if(forId) sel = document.getElementById(forId);
      if(!sel) sel = label.parentElement && label.parentElement.querySelector('select');
    }
    if(!sel) sel = Array.from(document.querySelectorAll('select')).find(s=> (s.previousElementSibling && s.previousElementSibling.textContent||'').includes(labelText));
    if(!sel) return false;
    const opt = Array.from(sel.options).find(o=> o.text.includes(optionText));
    if(opt){ sel.value = opt.value; sel.dispatchEvent(new Event('change', { bubbles: true })); return true; }
    return false;
  }

  // Data to fill - edit as needed
  const data = {
    language: 'Talon',
    category: 'Snippet',
    display: 'Test Display',
    remarks: 'Automated test remarks',
    snippet: 'Test snippet with Variable1'
  };

  const results = {};
  results.language = selectByLabelText('Language or Main Category', data.language);
  results.category = selectByLabelText('Category / Subcategory', data.category);
  results.display = setByPlaceholder('Enter Display Value', data.display);
  results.remarks = setByPlaceholder('Enter Remarks', data.remarks);
  results.snippet = setByPlaceholder('Enter Send Keys Value', data.snippet);

  console.log('quickFill results:', results);
  // read back values
  try{
    const read = {
      language: (Array.from(document.querySelectorAll('select')).find(s=> s.previousElementSibling && s.previousElementSibling.textContent && s.previousElementSibling.textContent.includes('Language'))||{}).selectedOptions?.[0]?.text || null,
      category: (Array.from(document.querySelectorAll('select')).find(s=> s.previousElementSibling && s.previousElementSibling.textContent && s.previousElementSibling.textContent.includes('Category'))||{}).selectedOptions?.[0]?.text || null,
      display: document.querySelector('[placeholder="Enter Display Value"]')?.value || null,
      remarks: document.querySelector('[placeholder="Enter Remarks"]')?.value || null,
      snippet: document.querySelector('[placeholder="Enter Send Keys Value"]')?.value || null
    };
    console.log('read back:', read);
  }catch(e){ console.warn('read-back failed', e); }
})();
```

**Bookmarklet**
Create a bookmark in your browser and use this as the URL (single-line):

javascript:(function(){/* paste minified quickFill body here */})();

(For convenience you can copy the full function from above, remove newlines, and wrap in `javascript:(...)()`.)

**When to use CDP/Playwright in future**
- Use CDP/Playwright when you need repeatable, headless, or CI-driven tests, or when you need to preserve browser session state across runs.
- I scaffolded a Playwright example under `tools/PlaywrightAttachExample` in this repo; move to that approach when tests must be reliable.

**Next steps I can do**
- Create a small `tools/integrated-browser-fill/fill.js` file with the script for easy copy/paste. (I can add this now.)
- Convert the function into a bookmarklet automatically and save to the repo.
- Switch to Playwright automation and wire a CLI to run the script against `localhost`.

If you want, I can add the `fill.js` file to the repo now.