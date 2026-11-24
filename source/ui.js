'use strict';

const number = new Intl.NumberFormat('ru-RU', {maximumFractionDigits: 1});
function sizeText(bytes) {
  const units = ['Б', 'КиБ', 'МиБ', 'ГиБ', 'ТиБ', 'ПиБ', 'ЭиБ'];
  let size = bytes, unit = 0;
  while (size >= 1024 && unit < units.length - 1) { size /= 1024; unit++; }
  return number.format(size) + ' ' + units[unit];
}

function category(name) {
  const extension = name.includes('.') ? name.split('.').pop().toLowerCase() : '';
  const types = [
    ['Изображения', '#b3e7a2', 'jpg jpeg png webp avif heic heif gif bmp tiff svg raw dng'],
    ['Видео', '#9abfed', 'mp4 mov mkv avi m4v webm'],
    ['Аудио', '#d2b5ed', 'mp3 wav flac m4a ogg aac opus'],
    ['Документы', '#e9d08d', 'pdf doc docx txt md csv xlsx xls ppt pptx odt rtf'],
    ['Архивы', '#edab9f', 'zip rar 7z tar gz tgz age']
  ];
  const group = types.find(type => type[2].split(' ').includes(extension));
  return {label: group ? group[0] : 'Прочее', color: group ? group[1] : '#adb9b4',
    extension: /^[a-z0-9]{1,5}$/.test(extension) ? extension.toUpperCase() : 'ФАЙЛ'};
}

function setupDownloads() {
  const items = Array.from(document.querySelectorAll('#files > li'), row => {
    const type = category(row.querySelector('strong').textContent);
    const raw = row.dataset.bytes;
    const bytes = /^\d+$/.test(raw || '') && Number.isFinite(Number(raw)) ? Number(raw) : null;
    const input = row.querySelector('input[name="id"]');
    const tile = document.createElement('span');
    tile.className = 'tile';
    tile.setAttribute('aria-hidden', 'true');
    tile.textContent = type.extension;
    const thumbnail = row.querySelector('img');
    if (!thumbnail) row.prepend(tile);
    else {
      const replace = () => thumbnail.replaceWith(tile);
      thumbnail.addEventListener('error', replace, {once:true});
      if (thumbnail.complete && !thumbnail.naturalWidth) replace();
    }
    return {type, bytes, input};
  });
  const total = items.reduce((sum, item) => sum + (item.bytes || 0), 0);
  const known = items.every(item => item.bytes !== null);
  const useBytes = known && total > 0;
  const groups = new Map();
  for (const item of items) {
    const label = item.type.label;
    const group = groups.get(label) || {label, color:item.type.color, count:0, bytes:0};
    group.count++;
    group.bytes += item.bytes || 0;
    groups.set(label, group);
  }
  const track = document.getElementById('track'), key = document.getElementById('key');
  const byMeasure = group => useBytes ? group.bytes : group.count;
  for (const group of Array.from(groups.values()).sort((a,b) => byMeasure(b)-byMeasure(a))) {
    if (byMeasure(group) > 0) {
      const segment = document.createElement('i');
      segment.style.flex = String(byMeasure(group)) + ' 1 0%';
      segment.style.backgroundColor = group.color;
      track.append(segment);
    }
    const label = document.createElement('span');
    const swatch = document.createElement('i');
    swatch.className = 'swatch';
    swatch.style.backgroundColor = group.color;
    label.append(swatch, group.label + ' · ' + (useBytes ? sizeText(group.bytes) : number.format(group.count)));
    key.append(label);
  }
  document.getElementById('basis').textContent = useBytes ? 'Объём по типам файлов' : 'Количество по типам файлов';
  document.getElementById('total').textContent = 'Файлов: ' + number.format(items.length) + (known ? ' · ' + sizeText(total) : '');
  document.getElementById('composition').hidden = !items.length;
  function updateSelection() {
    const selected = items.filter(item => item.input.checked);
    const exact = selected.every(item => item.bytes !== null);
    document.getElementById('subset').disabled = !selected.length;
    document.getElementById('selected-info').textContent = selected.length
      ? 'Выбрано: ' + number.format(selected.length) + ' из ' + number.format(items.length) +
        (exact ? ' · ' + sizeText(selected.reduce((sum,item) => sum + item.bytes, 0)) : '')
      : 'Отметьте файлы для отдельного ZIP.';
  }
  document.getElementById('files').addEventListener('change', updateSelection);
  window.addEventListener('pageshow', updateSelection);
  updateSelection();
}

function setupUpload() {
  const pick = document.getElementById('pick'), button = document.getElementById('start');
  const status = document.getElementById('status'), bar = document.getElementById('bar');
  const progress = document.getElementById('progress');
  const collect = document.body.dataset.collect === 'yes';
  let offset = 0, uploadId = '', sending = false;

  function showProgress(total, complete) {
    bar.max = total || 1;
    bar.value = complete ? bar.max : offset;
    document.getElementById('bytes').textContent = sizeText(offset) + ' из ' + sizeText(total);
    document.getElementById('percent').textContent = (complete ? 100 : Math.floor(100 * offset / (total || 1))) + '%';
  }
  pick.addEventListener('change', () => {
    offset = 0; uploadId = '';
    progress.hidden = true;
    status.textContent = '';
    const file = pick.files[0];
    document.getElementById('file-size').textContent = file ? sizeText(file.size) : '';
    button.disabled = !file;
    button.textContent = 'Отправить';
  });
  async function post(path, options={}) {
    let response;
    try { response = await fetch(path, Object.assign({method:'POST'}, options)); }
    catch { throw Error('Связь потеряна. Проверьте интернет и нажмите «Повторить».'); }
    if (response.ok) return response.text();
    if (response.status === 507) throw Error('На компьютере получателя недостаточно места.');
    if (response.status === 409) throw Error('Компьютер отклонил смещение блока. Попросите новую ссылку для повторной отправки.');
    if (response.status === 404 || response.status === 410) throw Error('Ссылка недоступна. Попросите владельца проверить передачу.');
    if (response.status === 400 && (response.headers.get('content-type') || '').startsWith('text/plain')) {
      throw Error((await response.text()).slice(0,160) || 'Компьютер отклонил блок.');
    }
    throw Error('Получатель не подтвердил запрос (HTTP ' + response.status + '). Попробуйте ещё раз.');
  }
  button.addEventListener('click', async () => {
    const file = pick.files[0];
    if (!file || sending) return;
    if (!window.crypto || !crypto.subtle) { status.textContent = 'Откройте HTTPS-ссылку в браузере с поддержкой проверки SHA-256.'; return; }
    sending = true; pick.disabled = button.disabled = true;
    button.textContent = 'Отправляем…'; status.textContent = '';
    progress.hidden = false; showProgress(file.size, false);
    try {
      if (collect && !uploadId) {
        const id = (await post('new-upload')).trim();
        if (!/^[a-f\d]+$/i.test(id)) throw Error('Компьютер не подтвердил начало передачи.');
        uploadId = id;
      }
      do {
        const block = await file.slice(offset, offset + 4*1024*1024).arrayBuffer();
        const digest = await crypto.subtle.digest('SHA-256', block);
        const hex = Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2,'0')).join('');
        const ack = (await post(collect ? 'chunk/' + uploadId : 'chunk', {
          headers:{'X-Name':encodeURIComponent(file.name), 'X-Size':String(file.size), 'X-Offset':String(offset), 'X-SHA256':hex},
          body:block
        })).trim();
        const next = offset + block.byteLength;
        if (ack !== String(next)) throw Error('Ответ не подтверждает запись блока. Проверьте связь и повторите.');
        offset = next;
        showProgress(file.size, offset === file.size);
      } while (offset < file.size);
      status.textContent = 'Готово. Файл сохранён на компьютере.';
      button.hidden = !collect;
      if (collect) {
        uploadId = ''; offset = 0; pick.value = ''; pick.disabled = false;
        button.textContent = 'Отправить'; document.getElementById('file-size').textContent = '';
      }
    } catch (error) {
      status.textContent = 'Передача остановлена. ' + error.message;
      button.textContent = 'Повторить'; button.disabled = false;
      // A lost acknowledgement can leave a first block committed. Retry the same file.
      pick.disabled = true;
    } finally { sending = false; }
  });
}
if (document.body.dataset.mode === 'send') setupDownloads(); else setupUpload();
