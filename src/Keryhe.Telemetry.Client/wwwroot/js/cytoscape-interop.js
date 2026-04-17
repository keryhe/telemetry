let _cy = null;
let _darkMode = false;

function buildStyle(darkMode, maxCount) {
    const bg = darkMode ? '#1e1e2e' : '#ffffff';
    const nodeColor = darkMode ? '#3a3a5c' : '#e8eaf6';
    const nodeBorder = darkMode ? '#7986cb' : '#3f51b5';
    const labelColor = darkMode ? '#e0e0e0' : '#212121';

    return [
        {
            selector: 'node',
            style: {
                'shape': 'round-rectangle',
                'background-color': nodeColor,
                'border-color': nodeBorder,
                'border-width': 2,
                'label': 'data(label)',
                'color': labelColor,
                'font-size': '12px',
                'text-valign': 'center',
                'text-halign': 'center',
                'width': 150,
                'height': 40
            }
        },
        {
            selector: 'edge',
            style: {
                'width': function (ele) {
                    const ratio = ele.data('callCount') / maxCount;
                    return Math.max(1, Math.round(ratio * 6));
                },
                'line-color': function (ele) {
                    const er = ele.data('errorRate');
                    if (er > 5) return '#f44336';
                    if (er > 1) return '#ff9800';
                    return '#4caf50';
                },
                'target-arrow-color': function (ele) {
                    const er = ele.data('errorRate');
                    if (er > 5) return '#f44336';
                    if (er > 1) return '#ff9800';
                    return '#4caf50';
                },
                'target-arrow-shape': 'triangle',
                'curve-style': 'bezier',
                'label': function (ele) {
                    return ele.data('avgDurationMs').toFixed(1) + 'ms';
                },
                'font-size': '10px',
                'color': labelColor,
                'text-background-opacity': 0.8,
                'text-background-color': bg,
                'text-background-padding': '2px'
            }
        }
    ];
}

export function init(containerId, elements, darkMode) {
    if (_cy) {
        _cy.destroy();
        _cy = null;
    }

    const container = document.getElementById(containerId);
    if (!container) return;

    _darkMode = darkMode;

    const counts = elements.filter(e => e.data.callCount != null).map(e => e.data.callCount);
    const maxCount = counts.length > 0 ? Math.max(...counts) : 1;

    const layout = {
        name: 'circle',
        padding: 60,
        animate: false
    };

    _cy = cytoscape({
        container,
        elements,
        style: buildStyle(darkMode, maxCount),
        layout: { name: 'preset' },
        userZoomingEnabled: true,
        userPanningEnabled: true,
        boxSelectionEnabled: false
    });

    // The container may be in a hidden tab (display:none) when init runs, giving it
    // zero dimensions. Use ResizeObserver to run the real layout the first time the
    // container becomes visible and has actual size.
    const observer = new ResizeObserver(entries => {
        for (const entry of entries) {
            if (entry.contentRect.width > 0 && entry.contentRect.height > 0) {
                observer.disconnect();
                _cy.layout(layout).run();
                _cy.fit(undefined, 40);
            }
        }
    });
    observer.observe(container);
}

export function update(elements) {
    if (!_cy) return;

    const counts = elements.filter(e => e.data.callCount != null).map(e => e.data.callCount);
    const maxCount = counts.length > 0 ? Math.max(...counts) : 1;

    _cy.elements().remove();
    _cy.add(elements);
    _cy.style(buildStyle(_darkMode, maxCount));
    _cy.layout({ name: 'circle', padding: 60, animate: false }).run();
    _cy.fit(undefined, 40);
}

export function destroy() {
    if (_cy) {
        _cy.destroy();
        _cy = null;
    }
}
