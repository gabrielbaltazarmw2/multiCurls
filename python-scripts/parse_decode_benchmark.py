import os
import re
import csv
from typing import Optional, Dict, Any, List

# >>> CONFIGURE AQUI <<<
LOG_FILE = r"C:\Users\Rafael\Desktop\decode_benchmark_log.txt"
OUT_CSV = r"C:\Users\Rafael\Desktop\decode_benchmark_samples.csv"

# Regex para linhas [DECODE]
DECODE_RE = re.compile(
    r"\[.*?\]\s+\[DECODE\]\s+(\d+)/(\d+)\s+file=([\w\.]+)\s+"
    r"decode_ms=([\d,]+)\s+total_ms=([\d,]+)"
)

# Regex para o bloco final de estatísticas (opcional)
SUMMARY_RE = {
    "files_decoded": re.compile(r"Files decoded:\s*(\d+)"),
    "total_wall_ms": re.compile(r"Total wall-clock time \(ms\):\s*([\d,]+)"),
    "avg_decode_ms": re.compile(r"Avg decode_ms:\s*([\d,]+)"),
    "median_decode_ms": re.compile(r"Median decode_ms:\s*([\d,]+)"),
    "p95_decode_ms": re.compile(r"P95 decode_ms:\s*([\d,]+)"),
    "avg_total_ms": re.compile(r"Avg total_ms \(read\+decode\+mesh\):\s*([\d,]+)"),
}

def parse_number_br(s: str) -> float:
    """Converte string com vírgula decimal para float."""
    s = s.strip().replace(".", "")  # se por acaso vier "1.234,56"
    s = s.replace(",", ".")
    return float(s)

def parse_log_file(path: str) -> (List[Dict[str, Any]], Dict[str, Any]):
    samples = []
    summary = {}

    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()

            # Tenta bater como linha de DECODE
            m = DECODE_RE.search(line)
            if m:
                idx = int(m.group(1))
                total = int(m.group(2))
                file_name = m.group(3)
                decode_ms = parse_number_br(m.group(4))
                total_ms = parse_number_br(m.group(5))

                samples.append({
                    "index": idx,
                    "total_frames": total,
                    "file": file_name,
                    "decode_ms": decode_ms,
                    "total_ms": total_ms,
                })
                continue

            # Tenta bater como alguma linha do resumo
            for key, regex in SUMMARY_RE.items():
                m2 = regex.search(line)
                if m2:
                    val_str = m2.group(1)
                    # "Files decoded" é inteiro, o resto pode ter vírgula decimal
                    if key == "files_decoded":
                        summary[key] = int(val_str)
                    else:
                        summary[key] = parse_number_br(val_str)

    return samples, summary

def write_csv(samples: List[Dict[str, Any]], csv_path: str):
    if not samples:
        print("[WARN] Nenhuma linha [DECODE] encontrada.")
        return

    fieldnames = ["index", "total_frames", "file", "decode_ms", "total_ms"]

    with open(csv_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for s in samples:
            writer.writerow(s)

    print(f"[INFO] CSV salvo em: {csv_path}")
    print(f"[INFO] Amostras: {len(samples)}")

if __name__ == "__main__":
    if not os.path.isfile(LOG_FILE):
        print(f"[ERRO] Arquivo de log não encontrado: {LOG_FILE}")
    else:
        samples, summary = parse_log_file(LOG_FILE)
        write_csv(samples, OUT_CSV)

        # Mostra o resumo lido do arquivo (para conferência)
        if summary:
            print("\n[Resumo lido do log]")
            for k, v in summary.items():
                print(f"  {k}: {v}")
