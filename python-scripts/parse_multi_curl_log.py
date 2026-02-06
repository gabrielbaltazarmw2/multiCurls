import os
import re
import csv
from typing import Optional, Dict, Any, Tuple

# ========= CONFIGURAÇÃO =========
# coloque aqui a pasta raiz onde estão os logs (equivalente ao "logs/")
ROOT_LOG_DIR = r"C:\Users\Rafael\Desktop\multiplosCurlsScripts\logs"

# caminho/nome do CSV de saída
OUTPUT_CSV = r"C:\Users\Rafael\Desktop\multiplosCurlsScripts\multi_curl_logs_summary.csv"
# ================================

# Regex para timestamp do início da linha: [18:11:41.488]
TIMESTAMP_RE = re.compile(r'^\[(\d{2}):(\d{2}):(\d{2})\.(\d{3})\]')

# Regex para linha de configuração: [..] BatchSize: 16 | MaxParallelBatches: 2
CFG_RE = re.compile(r'BatchSize:\s*(\d+)\s*\|\s*MaxParallelBatches:\s*(\d+)')

# Tags para linhas de início/fim de batch
BATCH_START_TAG = "[Batch START]"
BATCH_DONE_TAG = "[Batch DONE]"


def parse_timestamp_to_seconds(line: str) -> Optional[float]:
    """Extrai [HH:MM:SS.mmm] e converte para segundos (float)."""
    m = TIMESTAMP_RE.match(line)
    if not m:
        return None
    h, mnt, s, ms = map(int, m.groups())
    return h * 3600 + mnt * 60 + s + ms / 1000.0


def parse_timestamp_to_str(line: str) -> Optional[str]:
    """Extrai [HH:MM:SS.mmm] como string 'HH:MM:SS.mmm'."""
    m = TIMESTAMP_RE.match(line)
    if not m:
        return None
    h, mnt, s, ms = m.groups()
    return f"{h}:{mnt}:{s}.{ms}"


def parse_log_file(path: str) -> Optional[Dict[str, Any]]:
    """
    Lê um arquivo de log e extrai:
      - batch_size
      - max_parallel
      - horário do primeiro Batch START
      - horário do último Batch DONE
      - duração
      - contagem de batches start/done
    """
    batch_size = None
    max_parallel = None

    first_start_time_s = None
    first_start_time_str = None

    last_done_time_s = None
    last_done_time_str = None

    num_batch_start = 0
    num_batch_done = 0

    try:
        with open(path, "r", encoding="utf-8") as f:
            lines = f.readlines()
    except Exception as e:
        print(f"[WARN] Não foi possível ler {path}: {e}")
        return None

    for line in lines:
        line = line.rstrip("\n")

        # Configuração BatchSize / MaxParallel
        if "BatchSize:" in line:
            m_cfg = CFG_RE.search(line)
            if m_cfg:
                batch_size = int(m_cfg.group(1))
                max_parallel = int(m_cfg.group(2))

        # Primeiro Batch START
        if BATCH_START_TAG in line:
            ts_s = parse_timestamp_to_seconds(line)
            ts_str = parse_timestamp_to_str(line)
            if ts_s is not None:
                num_batch_start += 1
                if first_start_time_s is None or ts_s < first_start_time_s:
                    first_start_time_s = ts_s
                    first_start_time_str = ts_str

        # Último Batch DONE
        if BATCH_DONE_TAG in line:
            ts_s = parse_timestamp_to_seconds(line)
            ts_str = parse_timestamp_to_str(line)
            if ts_s is not None:
                num_batch_done += 1
                last_done_time_s = ts_s
                last_done_time_str = ts_str

    if first_start_time_s is None or last_done_time_s is None:
        print(f"[WARN] Arquivo {path} não possui Batch START ou Batch DONE suficientes, pulando.")
        return None

    # Se não achou config, tenta inferir do nome/pasta (bsXX_maxYY)
    if batch_size is None or max_parallel is None:
        base = os.path.basename(path)
        folder = os.path.dirname(path)
        combined = base + " " + folder
        m_fallback = re.search(r"bs(\d+)_max(\d+)", combined, re.IGNORECASE)
        if m_fallback:
            if batch_size is None:
                batch_size = int(m_fallback.group(1))
            if max_parallel is None:
                max_parallel = int(m_fallback.group(2))

    duration_s = last_done_time_s - first_start_time_s
    duration_ms = int(round(duration_s * 1000))

    return {
        "file_name": os.path.basename(path),
        # "file_path": os.path.abspath(path),
        "batch_size": batch_size,
        "max_parallel": max_parallel,
        "start_time": first_start_time_str,
        "end_time": last_done_time_str,
        "duration_seconds": duration_s,
        "duration_ms": duration_ms,
        # "num_batch_start": num_batch_start,
        # "num_batch_done": num_batch_done,
    }


def walk_logs(root_dir: str) -> Tuple[list, list]:
    """Varre root_dir recursivamente, parseia todos os .txt."""
    results = []
    errors = []

    for dirpath, dirnames, filenames in os.walk(root_dir):
        for fname in filenames:
            if not fname.lower().endswith(".txt"):
                continue
            full_path = os.path.join(dirpath, fname)
            info = parse_log_file(full_path)
            if info is not None:
                results.append(info)
            else:
                errors.append(full_path)

    return results, errors


def write_csv(rows: list, csv_path: str):
    if not rows:
        print("[WARN] Nenhum dado para escrever no CSV.")
        return

    fieldnames = [
        "file_name",
        # "file_path",
        "batch_size",
        "max_parallel",
        "start_time",
        "end_time",
        "duration_seconds",
        "duration_ms",
        # "num_batch_start",
        # "num_batch_done",
    ]

    os.makedirs(os.path.dirname(csv_path), exist_ok=True)

    with open(csv_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            writer.writerow(row)

    print(f"[INFO] CSV salvo em: {csv_path}")


if __name__ == "__main__":
    root = ROOT_LOG_DIR
    out_csv = OUTPUT_CSV

    print(f"[INFO] Varre logs em: {root}")
    rows, errs = walk_logs(root)
    write_csv(rows, out_csv)

    if errs:
        print("[WARN] Alguns arquivos não puderam ser processados:")
        for p in errs:
            print("  -", p)
