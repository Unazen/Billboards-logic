window.billboardDownloadFile = (fileName, contentType, base64Content) => {
    const binary = atob(base64Content);
    const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
    const blob = new Blob([bytes], { type: contentType });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
};
