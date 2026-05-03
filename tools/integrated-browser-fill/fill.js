// Quick Integrated Browser autofill helper
// Copy-paste into the Integrated Browser Console. Does NOT submit.
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
