import zlib, sys, os, pickle

args = [a for a in sys.argv[1:]]
list_only = "list" in args
args = [a for a in args if a != "list"]
rpa_path, out_dir = args[0], args[1]
filter_ext = args[2] if len(args) > 2 else None

with open(rpa_path, "rb") as f:
    header = f.readline().decode("utf-8").strip()
    if not header.startswith("RPA-3.0"):
        raise SystemExit("Not RPA-3.0: " + header)
    _, offset, keylen = header.split(" ", 2)
    offset = int(offset, 16)
    key = int(keylen, 16)
    f.seek(offset)
    index = pickle.loads(zlib.decompress(f.read()))

os.makedirs(out_dir, exist_ok=True)
names = sorted(index.keys())

if list_only:
    for n in names:
        if filter_ext is None or n.lower().endswith(filter_ext):
            print(n)
    print("TOTAL", len(names), file=sys.stderr)
    raise SystemExit(0)

count = 0
for name in names:
    if filter_ext is not None and not name.lower().endswith(filter_ext):
        continue
    data = b""
    for enc_offset, length, prefix in index[name]:
        with open(rpa_path, "rb") as f:
            f.seek(enc_offset ^ key)
            data += prefix + f.read(length - len(prefix))
    outpath = os.path.join(out_dir, name.replace("/", os.sep))
    d = os.path.dirname(outpath)
    if d:
        os.makedirs(d, exist_ok=True)
    with open(outpath, "wb") as wf:
        wf.write(data)
    count += 1
print("extracted", count, "files")
