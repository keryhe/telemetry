// Download file helper
window.downloadFile = function (filename, base64Content) {
    const link = document.createElement('a');
    link.download = filename;
    link.href = 'data:text/csv;base64,' + base64Content;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

window.localStorageGetItem = (key) => localStorage.getItem(key);
window.localStorageSetItem = (key, value) => localStorage.setItem(key, value);

window.getBrowserTimezone = () => Intl.DateTimeFormat().resolvedOptions().timeZone;

