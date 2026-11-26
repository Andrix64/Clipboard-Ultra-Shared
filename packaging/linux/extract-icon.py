"""Estrae da un file .ico l'immagine PNG di una dimensione (le icone di CUS hanno ogni dimensione in PNG).

Uso: python3 extract-icon.py <file.ico> <dimensione> <uscita.png>
"""
import struct
import sys

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def extract(ico_path: str, size: int) -> bytes:
    data = open(ico_path, "rb").read()
    _, kind, count = struct.unpack("<HHH", data[:6])
    if kind != 1:
        raise ValueError(f"{ico_path} non è un'icona")
    for i in range(count):
        width, height, _, _, _, _, length, offset = struct.unpack("<BBBBHHII", data[6 + 16 * i:22 + 16 * i])
        # 0 nella directory dell'icona vuol dire 256.
        if (width or 256) == size and (height or 256) == size:
            image = data[offset:offset + length]
            if not image.startswith(PNG_SIGNATURE):
                raise ValueError(f"l'immagine {size}x{size} non è un PNG")
            return image
    raise ValueError(f"nessuna immagine {size}x{size} in {ico_path}")


if __name__ == "__main__":
    ico, size, output = sys.argv[1], int(sys.argv[2]), sys.argv[3]
    open(output, "wb").write(extract(ico, size))
