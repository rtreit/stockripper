from playwright.sync_api import sync_playwright
import hashlib

with sync_playwright() as p:
    browser = p.chromium.launch()
    context = browser.new_context(ignore_https_errors=True)
    page = context.new_page()
    page.goto("https://steamcommmuty.com/")
    url_part = page.url.split('/')[2][0:10]
    unique_hash = hashlib.md5(page.url.encode()).hexdigest()[:8]
    filename = f"{url_part}_{unique_hash}.png"
    print(filename)
    
    page.screenshot(path=filename)
    browser.close()

    