import type { VideoStatus } from '../api/types';
import { statusLabel, statusTone } from '../lib/format';

export function StatusMark({ status }: { status: VideoStatus }) {
  return <span className={`status status--${statusTone(status)}`}>{statusLabel[status]}</span>;
}

