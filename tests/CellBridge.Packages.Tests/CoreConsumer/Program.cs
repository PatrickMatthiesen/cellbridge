using CellBridge.Storage;

var documents = new DocumentStore();
var document = documents.Put("/shared/core-package.txt", [1, 2, 3]);
return document.Content.Length == 3 ? 0 : 1;
