import os
import pandas as pd
import matplotlib.pyplot as plt
import numpy as np

# ==== CONFIGURAÇÕES ====
# Caminho para o CSV gerado pelo parser
CSV_PATH = r"C:\Users\Rafael\Desktop\multiplosCurlsScripts\multi_curl_logs_summary.csv"

# Caminho para salvar a figura
OUTPUT_FIG = r"C:\Users\Rafael\Desktop\multiplosCurlsScripts\plots\heatmap_batchsize_maxparallel.png"
# =======================


def main():
    # Lê o CSV
    df = pd.read_csv(CSV_PATH)

    # Garante tipos numéricos
    df["batch_size"] = pd.to_numeric(df["batch_size"], errors="coerce")
    df["max_parallel"] = pd.to_numeric(df["max_parallel"], errors="coerce")
    df["duration_seconds"] = pd.to_numeric(df["duration_seconds"], errors="coerce")

    # Remove linhas com NaN nas colunas importantes
    df = df.dropna(subset=["batch_size", "max_parallel", "duration_seconds"])

    # Agrupa por (batch_size, max_parallel) e calcula média de duração
    grouped = (
        df.groupby(["batch_size", "max_parallel"])["duration_seconds"]
          .mean()
          .reset_index()
    )

    print("Grouped mean durations:")
    print(grouped)

    # Cria uma tabela pivot: linhas = max_parallel, colunas = batch_size
    pivot = grouped.pivot(index="max_parallel", columns="batch_size", values="duration_seconds")

    # Ordena os índices/colunas
    pivot = pivot.sort_index(axis=0)  # max_parallel
    pivot = pivot.sort_index(axis=1)  # batch_size

    # Converte para numpy para o imshow
    data = pivot.values

    # Cria a figura
    plt.figure(figsize=(8, 5))

    # imshow: por padrão (0,0) é canto superior esquerdo, então invertendo o eixo y se quiser
    im = plt.imshow(data, aspect="auto", origin="lower")

    # Eixos com ticks = valores de batch_size / max_parallel
    x_labels = pivot.columns.to_list()
    y_labels = pivot.index.to_list()

    plt.xticks(ticks=np.arange(len(x_labels)), labels=x_labels)
    plt.yticks(ticks=np.arange(len(y_labels)), labels=y_labels)

    plt.xlabel("Batch size")
    plt.ylabel("Max parallel (number of curl processes)")
    plt.title("Heatmap: duration (s) vs batch_size and max_parallel")

    # Colorbar mostrando duração média em segundos
    cbar = plt.colorbar(im)
    cbar.set_label("Mean total duration (s)")

    # Opcional: grade leve
    plt.grid(False)

    # Salva figura
    if OUTPUT_FIG:
        os.makedirs(os.path.dirname(OUTPUT_FIG), exist_ok=True)
        plt.savefig(OUTPUT_FIG, dpi=200, bbox_inches="tight")
        print(f"Figure saved to: {OUTPUT_FIG}")

    plt.show()


if __name__ == "__main__":
    main()
