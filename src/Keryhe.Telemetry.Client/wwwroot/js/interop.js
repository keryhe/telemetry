// Download file helper
window.downloadFile = function (filename, base64Content) {
    const link = document.createElement('a');
    link.download = filename;
    link.href = 'data:text/csv;base64,' + base64Content;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

window.getChartSvgWidth = function (containerElement) {
    if (!containerElement) return 0;
    const svg = containerElement.querySelector('svg');
    if (!svg) return 0;
    return svg.getBoundingClientRect().width;
};

window.scrollDataGridToRow = function (containerElement, rowIndex, rowHeightPx) {
    if (!containerElement) return;
    const tableContainer = containerElement.querySelector('.mud-table-container');
    if (tableContainer) {
        tableContainer.scrollTop = rowIndex * rowHeightPx;
    }
};
