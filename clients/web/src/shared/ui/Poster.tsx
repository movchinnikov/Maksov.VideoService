import { useEffect, useState } from 'react';
import { fetchObjectUrl } from '../api/client';

/** The poster is protected by authorization, so &lt;img src&gt; will not work: fetch a blob with the token. */
export function Poster({ path, alt, className }: { path: string | null; alt: string; className?: string }) {
  const [url, setUrl] = useState<string | null>(null);

  useEffect(() => {
    if (!path) return;
    const controller = new AbortController();
    let objectUrl: string | null = null;
    fetchObjectUrl(path, controller.signal)
      .then((u) => {
        objectUrl = u;
        setUrl(u);
      })
      .catch(() => setUrl(null));
    return () => {
      controller.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [path]);

  if (!path || !url) {
    return (
      <span className={`${className ?? ''} thumb--empty`} aria-hidden="true">
        {path ? '…' : '—'}
      </span>
    );
  }
  return <img className={className} src={url} alt={alt} loading="lazy" />;
}

