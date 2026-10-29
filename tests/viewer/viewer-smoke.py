"""Clicks through a served map in a headless browser, as a visitor and as an admin.

Usage:  python viewer-smoke.py http://localhost:8000/ <admin password>

Needs Playwright (pip install playwright; playwright install chromium) and a map with at least one surface, as the
renderer's server mode serves it. Every check prints a line; the script ends with exit code 1 if one failed.
"""
import asyncio
import json
import sys
import urllib.error
import urllib.request

from playwright.async_api import async_playwright

URL = sys.argv[1].rstrip('/') + '/' if len(sys.argv) > 1 else 'http://localhost:8000/'
PASSWORD = sys.argv[2] if len(sys.argv) > 2 else ''
failures = []


def check(ok, what):
    print(('  ok    ' if ok else '  FAIL  ') + what)
    if not ok:
        failures.append(what)


def post(path, body=b'', headers=None):
    """A POST from outside the page; returns the status code."""
    request = urllib.request.Request(URL + path.lstrip('/'), data=body, method='POST', headers=headers or {})
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code


def get(path):
    try:
        with urllib.request.urlopen(URL + path.lstrip('/'), timeout=10) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, b''


async def wait_idle(page, ms=1500):
    await page.wait_for_load_state('networkidle')
    await page.wait_for_timeout(ms)


async def visitor(browser):
    print('Visitor')
    page = await browser.new_page(viewport={'width': 1500, 'height': 900})
    errors = []
    page.on('pageerror', lambda e: errors.append(str(e)))
    page.on('console', lambda m: errors.append(m.text) if m.type == 'error' else None)
    await page.goto(URL)
    await page.evaluate("localStorage.clear(); localStorage.setItem('fmr:news', 'false')")
    await page.goto(URL + '#')
    await wait_idle(page)

    data = await page.evaluate('window.MAP_DATA')
    check(not data.get('empty') and len(data['surfaces']) > 0, f"a map with {len(data['surfaces'])} surfaces")
    check(await page.title() != 'Factorio Map', f"the page is named after the save: {await page.title()!r}")
    chips = await page.locator('#planets [data-surface]').count()
    check(chips >= 2, f'{chips} surfaces to choose (solar system and planets)')
    platforms = [s for s in data['surfaces'] if s['kind'] == 'platform']
    check(await page.locator('#platform-items button').count() == len(platforms), f'{len(platforms)} space platforms in their list')

    # every surface can be shown, with its layers
    for surface in data['surfaces']:
        await page.evaluate(f"location.hash = '#{surface['name']}/5/0/0'")
        await page.wait_for_function(f"window.MAP_SURFACES && window.MAP_SURFACES[{surface['id']}]", timeout=15000)
        await page.wait_for_timeout(300)
        rows = await page.locator('#layers input[data-layer]:not(:disabled)').count()
        check(rows > 0, f"{surface['title']}: shown, {rows} layers")

    # the main planet: map view tiles load, markers, statistics
    main = max(data['surfaces'], key=lambda s: s['entities'])
    focus = main.get('focus') or main['bounds']
    cx, cy = (focus[0] + focus[2]) / 2, (focus[1] + focus[3]) / 2
    await page.evaluate(f"location.hash = '#{main['name']}/5/{cx:.0f}/{cy:.0f}'")
    await wait_idle(page)
    tiles = await page.evaluate("[...document.querySelectorAll('.tiles-terrain img')].filter(i => i.complete && i.naturalWidth > 0).length")
    check(tiles > 0, f'{main["title"]}: {tiles} map tiles of the ground loaded')
    broken = await page.evaluate("[...document.querySelectorAll('.leaflet-tile-container img')].filter(i => i.complete && i.naturalWidth === 0 && i.style.visibility !== 'hidden').length")
    check(broken == 0, 'no broken tile images')

    # close up: the game's own graphics, drawn in the browser
    surface = await page.evaluate(f'window.MAP_SURFACES[{main["id"]}]')
    machine = next((i for i in surface['inspect'] if i[2] >= 1.4), surface['inspect'][0] if surface['inspect'] else None)
    if machine:
        await page.evaluate(f"void factorioMap.setView([{machine[1]}, {machine[0]}], 10)")
        await wait_idle(page, 3000)
        # the tiles come from the page's workers as pictures: copied onto a canvas of its own to be looked at
        painted = await page.evaluate("""[...document.querySelectorAll('.tiles-graphics canvas, .tiles-ground canvas')].filter(c => {
            const copy = document.createElement('canvas');
            copy.width = c.width;
            copy.height = c.height;
            const ctx = copy.getContext('2d');
            ctx.drawImage(c, 0, 0);
            const d = ctx.getImageData(0, 0, c.width, c.height).data;
            for (let i = 3; i < d.length; i += 4 * 97) if (d[i] > 0) return true;
            return false; }).length""")
        check(painted > 0, f'close up: {painted} tiles drawn from the game graphics')
        await page.evaluate(f"void factorioMap.fire('click', {{ latlng: L.latLng({machine[1]}, {machine[0]}) }})")
        await page.wait_for_timeout(500)
        popup = await page.locator('.leaflet-popup-content').inner_text() if await page.locator('.leaflet-popup-content').count() else ''
        check(machine[3] in popup, f'a click on a building shows its details: {popup.splitlines()[0] if popup else "nothing"!r}')

        # a layer switched off is gone from the map
        await page.locator('input[data-layer="graphics"]').uncheck()
        gone = await page.evaluate("document.querySelectorAll('.tiles-graphics').length === 0")
        check(gone, 'switching off "Game graphics" removes it')
        await page.locator('input[data-layer="graphics"]').check()

    # search
    names = [m[2] for layer in surface['markers'].values() for m in layer['items'] if len(m[2]) > 3]
    if names:
        term = names[0][:4]
        await page.fill('#search', term)
        await page.wait_for_timeout(300)
        hits = await page.locator('#search-results li').count()
        check(hits > 0, f'search for {term!r}: {hits} hits')
        await page.locator('#search-results li').first.click()
        await page.wait_for_timeout(600)
        check(await page.locator('.leaflet-popup-content').count() == 1, 'a hit opens its popup')
        await page.fill('#search', '')

    # statistics
    await page.click('[data-tab="stats"]')
    produced = await page.locator('#production tr').count()
    counted = await page.locator('#counts tr').count()
    check(produced > 1 and counted > 1, f'statistics: {produced} products, {counted} kinds of objects')
    await page.click('[data-tab="layers"]')

    # the solar system
    if data.get('space'):
        await page.click('#planets [data-surface="space"]')
        await wait_idle(page, 2500)
        globes = await page.evaluate("[...document.querySelectorAll('img.space-planet')].filter(i => i.naturalWidth > 0).length")
        check(globes == len(data['space']['locations']), f"solar system: {globes} of {len(data['space']['locations'])} places shown")
        markers = await page.locator('.space-platform').count()
        check(markers == len(platforms), f'solar system: {markers} of {len(platforms)} platforms shown')
        if markers:
            await page.locator('.space-platform').first.click()
            await page.wait_for_timeout(1500)
            shown = await page.evaluate("decodeURIComponent(location.hash).split('/')[0]")
            check(shown.startswith('#platform'), f'a click on a platform opens it ({shown})')

    # visitors cannot load saves
    await page.click('[data-tab="save"]')
    check(await page.locator('#admin').is_visible(), 'the Save game tab offers the admin login, not the sources')
    check(not await page.locator('#source-controls').is_visible(), 'visitors do not see the save game sources')
    check(not [e for e in errors if 'favicon' not in e], f'no errors in the page{": " + errors[0] if errors else ""}')
    await page.close()


async def admin(browser):
    print('Admin')
    if not PASSWORD:
        print('  (skipped: no password given)')
        return
    page = await browser.new_page(viewport={'width': 1500, 'height': 900})
    await page.goto(URL)
    await page.evaluate("localStorage.setItem('fmr:news', 'false')")
    await page.click('[data-tab="save"]')
    await page.fill('#admin-password', 'wrong-password')
    await page.click('#admin-submit')
    await page.wait_for_timeout(1200)
    check(await page.locator('#admin-error').is_visible(), 'a wrong password is refused')
    await page.fill('#admin-password', PASSWORD)
    await page.click('#admin-submit')
    # the page reloads after logging in
    try:
        await page.wait_for_selector('#source-controls', state='visible', timeout=15000)
    except Exception:
        pass
    check(await page.locator('#source-controls').is_visible(), 'logged in: the save game sources are there')
    check(await page.locator('#admin-session').is_visible(), 'logged in: the session bar is there')
    rows = await page.locator('#summary tr').count()
    check(rows >= 5, f'details of the save: {rows} rows')
    await page.click('#admin-logout')
    try:
        await page.wait_for_selector('#admin', state='visible', timeout=15000)
    except Exception:
        pass
    check(await page.locator('#admin').is_visible() and not await page.locator('#source-controls').is_visible(), 'logged out again')
    await page.close()


def api():
    print('API')
    status, health = get('api/health')
    check(status == 200 and b'ok' in health, 'health check')
    check(post('api/upload?name=a.zip', b'x', {'X-Map-Renderer': '1'}) == 401, 'a visitor cannot upload a save')
    check(post('api/upload?name=a.zip', b'x') == 403, 'a request without the page header is refused')
    check(post('api/login', json.dumps({'password': 'x'}).encode(), {'X-Map-Renderer': '1', 'Origin': 'https://evil.example'}) == 403,
          'a request from another site is refused')
    check(get('../../etc/passwd')[0] == 404 and get('%2e%2e/%2e%2e/etc/passwd')[0] == 404, 'no files outside the map')
    status, source = get('api/source')
    check(status == 200 and json.loads(source).get('admin') is False and 'folderPath' not in json.loads(source),
          'visitors see no connection settings')


async def main():
    api()
    async with async_playwright() as p:
        browser = await p.chromium.launch()
        await visitor(browser)
        await admin(browser)
        await browser.close()
    print(f'{len(failures)} failed' if failures else 'all passed')
    sys.exit(1 if failures else 0)


asyncio.run(main())
