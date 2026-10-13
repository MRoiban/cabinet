export const escape = (value: string) => value.replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]!);
export function page(title: string, body: string, status = 200): Response {
  return new Response(`<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${escape(title)}</title>
<style>[hidden]{display:none!important}:root{color-scheme:light dark;font:16px system-ui;background:#121719;color:#e8efed}body{max-width:960px;margin:48px auto;padding:0 24px}h1{font-size:32px}h2{font-size:21px}p{line-height:1.6;color:#becdc7}section{background:#1d2527;border:1px solid #344344;border-radius:14px;padding:24px;margin:24px 0}input,button,.button{font:inherit;border-radius:7px;padding:10px 14px}input{background:#121719;color:inherit;border:1px solid #526563;max-width:100%;box-sizing:border-box}button,.button{background:#acf0ce;color:#123329;border:0;cursor:pointer;text-decoration:none;display:inline-block}a{color:#acf0ce}form{display:inline-flex;gap:12px;flex-wrap:wrap}table{border-collapse:collapse;width:100%}td,th{padding:12px;text-align:left;border-bottom:1px solid #344344}code{overflow-wrap:anywhere}.link{width:100%;margin:10px 0}small{color:#becdc7}.actions{display:flex;gap:12px;flex-wrap:wrap}details{margin:12px 0}</style>
<body><h1>${escape(title)}</h1>${body}</body></html>`, { status, headers: {
    "content-type": "text/html; charset=utf-8", "cache-control": "no-store", "referrer-policy": "same-origin",
    "x-content-type-options": "nosniff", "content-security-policy": "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'",
  } });
}
export function connectPage(): Response {
  return page("Cabinet Cloud", `<section><h2 id="heading">Connect this PC</h2><p id="intro">Open this link with Cabinet installed on your PC.</p><p><a class="button" id="open" hidden>Open in Cabinet</a></p><label id="linklabel" for="link">Connect link</label><input class="link" id="link" readonly><button id="copy">Copy link</button><p id="message" role="status"></p></section><p><span id="install">Install Cabinet with PowerShell:</span><br><code>irm https://raw.githubusercontent.com/MRoiban/cabinet/main/install.ps1 | iex</code></p>
<script>
const fr=navigator.language.toLowerCase().startsWith('fr');
const text=fr?{heading:'Connecter ce PC',intro:'Ouvrez ce lien sur un PC où Cabinet est installé.',open:'Ouvrir dans Cabinet',linklabel:'Lien de connexion',copy:'Copier le lien',install:'Installer Cabinet avec PowerShell :',missing:'Ce lien ne contient pas de jeton valide.',copied:'Lien copié.',failed:'Sélectionnez le lien et copiez-le manuellement.'}:{missing:'This link has no valid token.',copied:'Link copied.',failed:'Select the link and copy it manually.'};
if(fr){document.documentElement.lang='fr';for(const id of ['heading','intro','open','linklabel','copy','install'])document.getElementById(id).textContent=text[id];}
const token=location.hash.slice(1),button=document.getElementById('open'),link=document.getElementById('link'),message=document.getElementById('message');
if(/^cab_[A-Za-z0-9_-]{43}$/.test(token)){button.hidden=false;button.href='cabinet://connect?server='+encodeURIComponent(location.origin)+'&token='+token;link.value=location.origin+'/connect#'+token;}else{message.textContent=text.missing;document.getElementById('copy').disabled=true;}
document.getElementById('copy').onclick=async()=>{try{await navigator.clipboard.writeText(link.value);message.textContent=text.copied;}catch{link.select();message.textContent=text.failed;}};
</script>`);
}
