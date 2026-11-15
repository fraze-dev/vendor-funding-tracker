// Reports an element's width to .NET whenever it changes, so SVG charts can draw at real pixel size:
// axis text stays 11px on every screen instead of scaling with a fixed viewBox.
export function observeWidth(element, dotNetRef) {
    const observer = new ResizeObserver(entries => {
        const width = Math.round(entries[0].contentRect.width);
        if (width > 0) {
            dotNetRef.invokeMethodAsync("OnWidthChanged", width);
        }
    });
    observer.observe(element);
    return { dispose: () => observer.disconnect() };
}
