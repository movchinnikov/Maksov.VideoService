export interface UploadProgress {
  loaded: number;
  total: number;
  bytesPerSecond: number;
}

/**
 * PUT the file to MinIO via a presigned URL. XHR rather than fetch: fetch does not report upload progress.
 * The body does not pass through the API — the server spends no memory or CPU on it.
 */
export function putFile(
  url: string,
  file: File,
  headers: Record<string, string>,
  onProgress: (p: UploadProgress) => void,
  signal: AbortSignal,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const started = performance.now();
    xhr.open('PUT', url);
    for (const [name, value] of Object.entries(headers)) {
      xhr.setRequestHeader(name, value);
    }
    xhr.upload.onprogress = (e) => {
      const seconds = Math.max((performance.now() - started) / 1000, 0.001);
      onProgress({ loaded: e.loaded, total: e.total || file.size, bytesPerSecond: e.loaded / seconds });
    };
    xhr.onload = () => (xhr.status >= 200 && xhr.status < 300 ? resolve() : reject(new Error(`Storage responded with ${xhr.status}`)));
    xhr.onerror = () => reject(new Error('Connection to storage was interrupted.'));
    xhr.onabort = () => reject(new DOMException('Upload cancelled', 'AbortError'));
    signal.addEventListener('abort', () => xhr.abort(), { once: true });
    xhr.send(file);
  });
}

