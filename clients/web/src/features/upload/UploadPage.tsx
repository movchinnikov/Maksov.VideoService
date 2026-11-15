import { getMeCached } from '../../shared/lib/data';
import { profilePath } from '../../shared/lib/urls';
import { useRef, useState, type DragEvent } from 'react';
import { useNavigate } from 'react-router';
import { api, ApiError } from '../../shared/api/client';
import { fileSize, titleFromFileName } from '../../shared/lib/format';
import { putFile, type UploadProgress } from './putFile';

const accepted = ['video/mp4', 'video/quicktime', 'video/webm', 'video/x-matroska'];
const maxBytes = 2 * 1024 * 1024 * 1024;

type Step = 'idle' | 'reserve' | 'transfer' | 'queue' | 'done';
type StepState = 'pending' | 'active' | 'done' | 'error';

const order: Step[] = ['reserve', 'transfer', 'queue'];

function stepState(current: Step, step: Step, failed: boolean): StepState {
  if (current === 'done') return 'done';
  const ci = order.indexOf(current);
  const si = order.indexOf(step);
  if (si < ci) return 'done';
  if (si === ci) return failed ? 'error' : 'active';
  return 'pending';
}

export function UploadPage() {
  const navigate = useNavigate();
  const [file, setFile] = useState<File | null>(null);
  const [title, setTitle] = useState('');
  const [over, setOver] = useState(false);
  const [step, setStep] = useState<Step>('idle');
  const [progress, setProgress] = useState<UploadProgress | null>(null);
  const [error, setError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const busy = step !== 'idle' && step !== 'done' && !error;

  const choose = (f: File | undefined) => {
    setError(null);
    setStep('idle');
    if (!f) return;
    // Client-side check is only a hint; the server enforces the rules.
    const type = f.type || guessType(f.name);
    if (!accepted.includes(type)) {
      setError('This does not look like a video. MP4, MOV, WebM, MKV will work.');
      return;
    }
    if (f.size > maxBytes) {
      setError(`The file is ${fileSize(f.size)} — over the 2 GB limit.`);
      return;
    }
    setFile(f);
    setTitle((t) => t || titleFromFileName(f.name));
  };

  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setOver(false);
    choose(e.dataTransfer.files[0]);
  };

  const start = async () => {
    if (!file) return;
    const controller = new AbortController();
    abortRef.current = controller;
    setError(null);
    try {
      setStep('reserve');
      const contentType = file.type || guessType(file.name);
      const upload = await api.createUpload({ title: title.trim(), fileName: file.name, contentType, sizeBytes: file.size });

      setStep('transfer');
      await putFile(upload.uploadUrl, file, upload.uploadHeaders, setProgress, controller.signal);

      setStep('queue');
      await api.completeUpload(upload.videoId);
      setStep('done');
      const me = await getMeCached().catch(() => null);
      navigate(me ? profilePath(me.id) : `/videos/${upload.videoId}`);
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') {
        setStep('idle');
        setProgress(null);
        return;
      }
      setError(e instanceof ApiError || e instanceof Error ? e.message : 'Upload failed.');
    }
  };

  const percent = progress ? Math.round((progress.loaded / progress.total) * 100) : 0;

  return (
    <>
      <div className="page-head">
        <h1>Upload</h1>
        <p className="muted">The file goes straight to storage. After upload the video will be cut into qualities and appear in “My videos”.</p>
      </div>

      <div className="desk">
        <div>
          <label
            className="drop"
            data-over={over}
            onDragOver={(e) => {
              e.preventDefault();
              setOver(true);
            }}
            onDragLeave={() => setOver(false)}
            onDrop={onDrop}
          >
            <input
              ref={inputRef}
              type="file"
              accept={accepted.join(',')}
              disabled={busy}
              onChange={(e) => choose(e.target.files?.[0])}
            />
            {file ? (
              <span>
                <span style={{ fontFamily: 'var(--font-display)', fontSize: 'var(--fs-28)' }}>{file.name}</span>
                <br />
                <span className="data muted">
                  {fileSize(file.size)} · {file.type || 'type unknown'}
                </span>
              </span>
            ) : (
              <span>
                <span style={{ fontFamily: 'var(--font-display)', fontSize: 'var(--fs-28)' }}>Drop a video here</span>
                <br />
                <span className="data muted">or click to choose · vertical 9:16 works best, up to 10 minutes, up to 2 GB</span>
              </span>
            )}
          </label>
          {error && (
            <div className="error-line" role="alert">
              {error}
            </div>
          )}
        </div>

        <div>
          <div className="field">
            <label htmlFor="title">Title</label>
            <input
              id="title"
              type="text"
              value={title}
              maxLength={200}
              disabled={busy}
              onChange={(e) => setTitle(e.target.value)}
              placeholder="What shall we call it?"
            />
          </div>

          <ol className="steps" aria-live="polite">
            <li data-state={stepState(step, 'reserve', !!error)}>
              Reserving space <span className="data" />
            </li>
            <li data-state={stepState(step, 'transfer', !!error)}>
              <span>
                Transferring file
                {step === 'transfer' && progress && (
                  <div className="meter" role="progressbar" aria-valuenow={percent} aria-valuemin={0} aria-valuemax={100}>
                    <span style={{ width: `${percent}%` }} />
                  </div>
                )}
              </span>
              <span className="data">{step === 'transfer' && progress ? `${percent}% · ${fileSize(progress.bytesPerSecond)}/s` : ''}</span>
            </li>
            <li data-state={stepState(step, 'queue', !!error)}>
              Queueing for processing <span className="data" />
            </li>
          </ol>

          <p style={{ display: 'flex', gap: 'var(--space-2)', marginTop: 'var(--space-4)' }}>
            <button type="button" className="btn" disabled={!file || !title.trim() || busy} onClick={() => void start()}>
              <span className="rec-dot" aria-hidden="true" /> {error ? 'Try again' : 'Upload'}
            </button>
            {busy && step === 'transfer' && (
              <button type="button" className="btn btn--ghost" onClick={() => abortRef.current?.abort()}>
                Cancel
              </button>
            )}
          </p>
        </div>
      </div>
    </>
  );
}

function guessType(name: string): string {
  const ext = name.split('.').pop()?.toLowerCase();
  switch (ext) {
    case 'mp4':
    case 'm4v':
      return 'video/mp4';
    case 'mov':
      return 'video/quicktime';
    case 'webm':
      return 'video/webm';
    case 'mkv':
      return 'video/x-matroska';
    default:
      return '';
  }
}

