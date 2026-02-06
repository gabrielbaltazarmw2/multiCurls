import pandas as pd
import matplotlib.pyplot as plt
import os

# ==== CONFIGURAÇÕES ====
# Caminho para o CSV gerado pelo parser
CSV_PATH = r"C:\Users\Rafael\Desktop\multiplosCurlsScripts\multi_curl_logs_summary.csv"

# Se quiser salvar a figura em arquivo:
OUTPUT_FIG = r"C:\Users\Rafael\Desktop\multiplosCurlsScripts\plots\duration_vs_batchsize.png"
# =======================


def main():
    # Lê o CSV
    df = pd.read_csv(CSV_PATH)

    # Garante que as colunas numéricas estão no tipo certo
    df["batch_size"] = pd.to_numeric(df["batch_size"], errors="coerce")
    df["max_parallel"] = pd.to_numeric(df["max_parallel"], errors="coerce")
    df["duration_seconds"] = pd.to_numeric(df["duration_seconds"], errors="coerce")

    # Remove linhas com valores inválidos
    df = df.dropna(subset=["batch_size", "max_parallel", "duration_seconds"])

    # Agrupa por (batch_size, max_parallel) para fazer média (e desvio) dos vários runs
    grouped = (
        df.groupby(["batch_size", "max_parallel"])["duration_seconds"]
          .agg(["mean", "std", "count"])
          .reset_index()
    )

    print("Grouped stats:")
    print(grouped)

    # Cria figura
    plt.figure(figsize=(8, 5))

    # Uma linha por max_parallel
    for max_par in sorted(grouped["max_parallel"].unique()):
        sub = grouped[grouped["max_parallel"] == max_par].copy()
        sub = sub.sort_values("batch_size")

        # Plot simples com média (sem barras de erro)
        # plt.plot(
        #     sub["batch_size"],
        #     sub["mean"],
        #     marker="o",
        #     label=f"max_parallel = {int(max_par)}"
        # )

        # Se quiser barras de erro (desvio padrão), descomente:
        plt.errorbar(
            sub["batch_size"],
            sub["mean"],
            yerr=sub["std"], 
            fmt="o-",
            capsize=4,
            label=f"max_parallel = {int(max_par)}"
        )

    plt.xlabel("Batch size")
    plt.ylabel("Total download duration (s)")
    plt.title("Duration vs Batch Size for different levels of parallelism")
    plt.grid(True, alpha=0.3)
    plt.legend()

    # Cria pasta de saída se não existir
    if OUTPUT_FIG:
        os.makedirs(os.path.dirname(OUTPUT_FIG), exist_ok=True)
        plt.savefig(OUTPUT_FIG, dpi=200, bbox_inches="tight")
        print(f"Figure saved to: {OUTPUT_FIG}")

    # Mostra na tela
    plt.show()


if __name__ == "__main__":
    main()
