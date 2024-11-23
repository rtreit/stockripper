import fitz  # PyMuPDF
import os
import re

# Ensure output directory exists
os.makedirs("text", exist_ok=True)

pdf_folder = "pdf"
text_folder = "text"

for filename in os.listdir(pdf_folder):
    if filename.endswith(".pdf"):
        pdf_path = os.path.join(pdf_folder, filename)
        text_filename = filename.replace(".pdf", ".txt")
        text_path = os.path.join(text_folder, text_filename)

        doc = fitz.open(pdf_path)
        text = ""

        for page_num in range(len(doc)):
            page = doc[page_num]
            page_text = page.get_text()

            # Remove footnotes and references by filtering short lines or numbered lines
            filtered_text = ""
            for line in page_text.splitlines():
                # Skip lines that seem like footnotes or are too short
                if re.match(r"^\d+\.", line) or len(line) < 15:
                    continue
                filtered_text += line + " "

            text += filtered_text + "\n\n"  # Preserve paragraph breaks
            print(f"Processed page {page_num + 1} of {len(doc)} in {filename}")

        # Save cleaned text to file
        with open(text_path, "w", encoding="utf-8") as f:
            f.write(text)

        print(f"Saved cleaned text to {text_path}")
