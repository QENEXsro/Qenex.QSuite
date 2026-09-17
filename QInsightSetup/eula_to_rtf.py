"""Converts the approved QENEX EULA plain-text files into RTF for the NSIS/Inno installer
license page. Paragraphs = blocks separated by blank lines (wrapped lines are joined);
a one-line, all-uppercase block is a heading; the very first block is the document title.
Output: UTF-8-agnostic RTF (non-ASCII as \\uN? escapes), Calibri 11 pt, justified."""
import io, re, sys

SRC = {
    'en': r'D:\GoogleDrive\Agents\QenexAi\Standa\KnowledgeBase\Legal\QENEX-Software-License-Agreement.txt',
    'cs': r'D:\GoogleDrive\Agents\QenexAi\Standa\KnowledgeBase\Legal\QENEX-Licencni-smlouva.txt',
}
DST = {
    'en': r'D:\Projects\Qenex\Source\Qenex.QSuite\QInsightSetup\QInsightLicense-en.rtf',
    'cs': r'D:\Projects\Qenex\Source\Qenex.QSuite\QInsightSetup\QInsightLicense-cs.rtf',
}
LANG = {'en': 2057, 'cs': 1029}  # \lang: en-GB, cs-CZ


def rtf_escape(text):
    out = []
    for ch in text:
        code = ord(ch)
        if ch in '\\{}':
            out.append('\\' + ch)
        elif code < 128:
            out.append(ch)
        else:
            if code > 0x7FFF:
                code -= 0x10000
            out.append('\\u%d?' % code)
    return ''.join(out)


def blocks(text):
    text = text.replace('\r\n', '\n').replace('\ufeff', '')
    result, cur = [], []
    for line in text.split('\n'):
        if line.strip() == '':
            if cur:
                result.append(cur)
                cur = []
        else:
            cur.append(line.rstrip())
    if cur:
        result.append(cur)
    return result


def is_heading(block):
    if len(block) != 1:
        return False
    line = block[0]
    return line == line.upper() and re.search(r'[A-ZÁ-Ž]', line) is not None and len(line) < 80


def is_list_item(block):
    return re.match(r'^\([a-z]\)\s', block[0]) is not None


def convert(lang):
    text = io.open(SRC[lang], encoding='utf-8').read()
    bl = blocks(text)
    parts = []
    parts.append('{\\rtf1\\ansi\\ansicpg1250\\deff0\\nouicompat\\deflang%d' % LANG[lang])
    parts.append('{\\fonttbl{\\f0\\fswiss\\fprq2\\fcharset0 Calibri;}}')
    parts.append('{\\colortbl ;\\red0\\green0\\blue0;}')
    parts.append('\\viewkind4\\uc1')
    for i, b in enumerate(bl):
        joined = ' '.join(l.strip() for l in b)
        esc = rtf_escape(joined)
        if i == 0:
            # document title
            parts.append('\\pard\\sa120\\sl252\\slmult1\\qc\\b\\f0\\fs28\\lang%d %s\\par' % (LANG[lang], esc))
        elif i in (1, 2):
            # subtitle and version line, centred
            parts.append('\\pard\\sa%d\\sl252\\slmult1\\qc\\b0\\f0\\fs22 %s\\par' % (60 if i == 1 else 240, esc))
        elif is_heading(b):
            parts.append('\\pard\\sb200\\sa120\\sl252\\slmult1\\ql\\keepn\\b\\f0\\fs22 %s\\par' % esc)
        elif is_list_item(b):
            parts.append('\\pard\\li360\\fi-360\\sa80\\sl252\\slmult1\\qj\\b0\\f0\\fs22 %s\\par' % esc)
        else:
            parts.append('\\pard\\sa120\\sl252\\slmult1\\qj\\b0\\f0\\fs22 %s\\par' % esc)
    parts.append('}')
    rtf = '\r\n'.join(parts) + '\r\n'
    io.open(DST[lang], 'w', encoding='ascii', newline='').write(rtf)
    headings = [b[0] for b in bl if is_heading(b)]
    print(f'{lang}: {len(bl)} blocks, {len(headings)} headings, {len(rtf)} bytes -> {DST[lang]}')
    return bl


if __name__ == '__main__':
    for lang in sys.argv[1:] or ['en', 'cs']:
        convert(lang)
