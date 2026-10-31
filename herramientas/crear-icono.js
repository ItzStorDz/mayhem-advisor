// Crea el icono del programa (icono.ico con 16-256 px y icono.png de 256 px) a partir del logo hexagonal.
// Uso: overlay-electron\node_modules\electron\dist\electron.exe herramientas\crear-icono.js
// Dibuja el SVG en un lienzo de cada tamaño y junta los PNG en un .ico (Windows acepta PNG dentro del .ico).
const { app, BrowserWindow } = require('electron');
const fs = require('node:fs');
const path = require('node:path');

const SIZES = [16, 24, 32, 48, 64, 128, 256];
const LOGO = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><defs>
  <linearGradient id="lg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ffe7a3"/><stop offset=".55" stop-color="#e0a84a"/><stop offset="1" stop-color="#8a5a1c"/></linearGradient>
  <linearGradient id="lg2" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#1b2230"/><stop offset="1" stop-color="#0a0d14"/></linearGradient></defs>
  <polygon points="32,2 58,17 58,47 32,62 6,47 6,17" fill="url(#lg)"/>
  <polygon points="32,7 53.5,19.5 53.5,44.5 32,57 10.5,44.5 10.5,19.5" fill="url(#lg2)"/>
  <path d="M18 44 V22 l14 13 14-13 v22" fill="none" stroke="url(#lg)" stroke-width="5" stroke-linejoin="round" stroke-linecap="round"/></svg>`;

const root = path.resolve(__dirname, '..');
const outputs = [path.join(root, 'src', 'Overlay', 'icono.ico'), path.join(root, 'overlay-electron', 'icono.ico')];

app.whenReady().then(async () => {
  const win = new BrowserWindow({ show: false });
  await win.loadURL('data:text/html,<html></html>');
  const pngs = await win.webContents.executeJavaScript(`(async () => {
    const img = new Image();
    img.src = 'data:image/svg+xml;base64,' + ${JSON.stringify(Buffer.from(LOGO).toString('base64'))};
    await img.decode();
    return ${JSON.stringify(SIZES)}.map(s => {
      const c = document.createElement('canvas'); c.width = c.height = s;
      c.getContext('2d').drawImage(img, 0, 0, s, s);
      return c.toDataURL('image/png').split(',')[1];
    });
  })()`);
  const images = pngs.map(b64 => Buffer.from(b64, 'base64'));

  // Formato .ico: cabecera (6 bytes) + una entrada de 16 bytes por imagen + los PNG.
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(images.length, 4);
  let offset = 6 + 16 * images.length;
  const entries = images.map((png, i) => {
    const e = Buffer.alloc(16), s = SIZES[i];
    e.writeUInt8(s >= 256 ? 0 : s, 0); e.writeUInt8(s >= 256 ? 0 : s, 1);
    e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6);
    e.writeUInt32LE(png.length, 8); e.writeUInt32LE(offset, 12);
    offset += png.length;
    return e;
  });
  const ico = Buffer.concat([header, ...entries, ...images]);
  for (const out of outputs) fs.writeFileSync(out, ico);
  fs.writeFileSync(path.join(root, 'overlay-electron', 'icono.png'), images[images.length - 1]);
  console.log('Icono creado:', outputs.join(', '));
  app.quit();
});
