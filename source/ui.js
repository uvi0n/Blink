const pick=document.querySelector('#pick'), button=document.querySelector('#start'), statusLine=document.querySelector('#status'), bar=document.querySelector('#bar');
let offset=0;
pick.onchange=()=>{offset=0;statusLine.textContent='';};
button.onclick=async()=>{
  const file=pick.files[0];
  if(!file){statusLine.textContent='Сначала выберите файл.';return;}
  pick.disabled=button.disabled=true;bar.hidden=false;bar.max=file.size||1;
  try{
    do {
      const block=await file.slice(offset,offset+4*1024*1024).arrayBuffer();
      const digest=await crypto.subtle.digest('SHA-256',block);
      const hex=Array.from(new Uint8Array(digest),v=>v.toString(16).padStart(2,'0')).join('');
      const res=await fetch('chunk',{method:'POST',headers:{'X-Name':encodeURIComponent(file.name),'X-Size':String(file.size),'X-Offset':String(offset),'X-SHA256':hex},body:block});
      if(!res.ok)throw new Error((await res.text()).slice(0,150)||'Ссылка недоступна.');
      const next=Number(await res.text());
      if(next!==offset+block.byteLength)throw new Error('Ошибка подтверждения.');
      offset=next;bar.value=offset;statusLine.textContent='Получено '+Math.round(100*offset/(file.size||1))+'%';
    }while(offset<file.size);
    bar.value=bar.max;statusLine.textContent='Готово. Файл сохранён на компьютере.';button.hidden=true;
  }catch(err){statusLine.textContent='Передача остановлена. '+err.message;button.textContent='Повторить';button.disabled=false;pick.disabled=offset>0;}
};

const selectedButton=document.getElementById('subset');
function updateSelection(){
  const count=document.querySelectorAll('input[name=id]:checked').length;
  selectedButton.disabled=!count;
  selectedButton.textContent=count?'Скачать выбранные ('+count+')':'Скачать выбранные';
}
document.addEventListener('change',updateSelection);
window.addEventListener('pageshow',updateSelection);
