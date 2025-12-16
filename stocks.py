# pip install pandas matplotlib pandas-datareader

from datetime import date
import pandas as pd
import matplotlib.pyplot as plt
from pandas_datareader import data as pdr

START = "2004-08-19"   # youngest company (Google IPO)
END   = date.today().isoformat()

TICKERS = {
    "Microsoft": "MSFT.US",
    "Intel":    "INTC.US",
    "IBM":       "IBM.US",
    "Oracle":    "ORCL.US",
    "Google":    "GOOGL.US",
    "Apple":     "AAPL.US",
    "Amazon":    "AMZN.US",
    "Meta":      "META.US",
    "NVIDIA":    "NVDA.US"
}

def load_close(symbol: str) -> pd.Series:
    df = pdr.DataReader(symbol, "stooq", START, END)
    df = df.sort_index()
    return df["Close"]

# Load prices
prices = pd.concat(
    {name: load_close(sym) for name, sym in TICKERS.items()},
    axis=1
).dropna()

# Normalize to 100 at start
normalized = prices / prices.iloc[0] * 100

# Plot
plt.figure(figsize=(12, 6))
for col in normalized.columns:
    plt.plot(normalized.index, normalized[col], label=col)

plt.title("Stock Price Performance (Normalized to 100)\nAug 19, 2004 – Present")
plt.ylabel("Indexed Price (Start = 100)")
plt.xlabel("Year")
plt.legend()
plt.grid(alpha=0.3)
plt.tight_layout()
plt.savefig("overlay_normalized_prices.png", dpi=200)
plt.show()
