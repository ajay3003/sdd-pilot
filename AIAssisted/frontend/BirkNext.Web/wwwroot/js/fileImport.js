window.fileImport = {
    initDropZone: function (element, dotNetRef) {
        element.addEventListener('drop', async function (e) {
            e.preventDefault();
            e.stopPropagation();

            var files = e.dataTransfer && e.dataTransfer.files;
            if (!files || files.length === 0) return;

            var file = files[0];
            try {
                var buffer = await file.arrayBuffer();
                var bytes = new Uint8Array(buffer);
                var encoding = 'utf-8';
                var sliceStart = 0;

                if (bytes.length >= 3 && bytes[0] === 0xEF && bytes[1] === 0xBB && bytes[2] === 0xBF) {
                    sliceStart = 3; // UTF-8 BOM: strip before decoding
                } else if (bytes.length >= 2 && bytes[0] === 0xFF && bytes[1] === 0xFE) {
                    encoding = 'utf-16le';
                    sliceStart = 2;
                } else if (bytes.length >= 2 && bytes[0] === 0xFE && bytes[1] === 0xFF) {
                    encoding = 'utf-16be';
                    sliceStart = 2;
                }

                // fatal:true throws on invalid byte sequences rather than substituting replacement chars
                var decoder = new TextDecoder(encoding, { fatal: true });
                var text = decoder.decode(sliceStart > 0 ? buffer.slice(sliceStart) : buffer);
                await dotNetRef.invokeMethodAsync('OnFileDrop', file.name, file.size, text);
            } catch {
                await dotNetRef.invokeMethodAsync('OnFileDropError');
            }
        });
    }
};
