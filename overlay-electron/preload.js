const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('mayhem', {
  onData: callback => ipcRenderer.on('data', (_event, message) => callback(message)),
  // Botones del overlay: dónde están (para capturar el ratón solo encima de ellos) y avisar al programa en C#.
  setButtons: rects => ipcRenderer.send('buttons', rects),
  send: message => ipcRenderer.send('to-app', message),
  // Arrastrando el panel: el overlay no debe soltar el ratón aunque el cursor salga del panel.
  setDragging: on => ipcRenderer.send('dragging', !!on),
  // Selección de campeón: dónde están sus botones y la lista (ahí la ventana coge el ratón; fuera, pasa al cliente de LoL).
  setChampRects: rects => ipcRenderer.send('champ-rects', rects),
});
