import {
  initializeCodec,
  inspectImage,
  decodeImage,
  encodeImage,
} from "./codec-engine.mjs";
self.onmessage = async (event) => {
  const { id, operation, data, frame, image, format, options } = event.data;
  try {
    await initializeCodec();
    const value =
      operation === "inspect"
        ? inspectImage(data)
        : operation === "decode"
          ? decodeImage(data, frame)
          : operation === "encode"
            ? encodeImage(image, format, options)
            : (() => {
                throw new Error("Unknown codec operation");
              })();
    self.postMessage({ id, value });
  } catch (error) {
    self.postMessage({ id, error: error.message });
  }
};
