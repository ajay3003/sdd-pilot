window.fileImport = {
    initDropZone: function (element, dotNetRef) {
        element.addEventListener('drop', async function (e) {
            e.preventDefault();
            e.stopPropagation();

            var files = e.dataTransfer && e.dataTransfer.files;
            if (!files || files.length === 0) return;

            var file = files[0];
            try {
                var text = await file.text();
                await dotNetRef.invokeMethodAsync('OnFileDrop', file.name, file.size, text);
            } catch {
                await dotNetRef.invokeMethodAsync('OnFileDropError');
            }
        });
    }
};
