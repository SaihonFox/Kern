import re,os,glob,collections
R='src/Kern.Runtime'
mm=dict()
for m in re.finditer(r'KernModule\("([^"]+)", "([^"]+)"\)',open(R+'/ModuleMap.cs',encoding='utf-8').read()): mm[m.group(2)]=m.group(1)
def modof(ns):
	best=None
	for k in mm:
		if ns==k or ns.startswith(k+'.'):
			if best is None or len(k)>len(best): best=k
	return mm.get(best)
exports=collections.defaultdict(set)
for f in glob.glob(R+'/**/*.cs',recursive=True):
	t=open(f,encoding='utf-8').read()
	ns=re.search(r'^namespace ([\w.]+)',t,re.M)
	if not ns: continue
	mod=modof(ns.group(1))
	for m in re.finditer(r'\[Kern(?:Type|Module)\("([^"]+)"',t):
		exports[mod].add(m.group(1))
print("modules with exports:",{k:sorted(v) for k,v in exports.items() if k})
print("namespaces without module:",sorted({re.search(r'^namespace ([\w.]+)',open(f,encoding='utf-8').read(),re.M).group(1) for f in glob.glob(R+'/**/*.cs',recursive=True) if re.search(r'^namespace ([\w.]+)',open(f,encoding='utf-8').read(),re.M) and not modof(re.search(r'^namespace ([\w.]+)',open(f,encoding='utf-8').read(),re.M).group(1))}))
# examples + tests
for f in glob.glob('examples/**/*.kern',recursive=True)+glob.glob('lib/**/*.kern',recursive=True):
	for i,l in enumerate(open(f,encoding='utf-8'),1):
		m=re.match(r'\s*use\s+([\w.]+)(?:\[([^\]]*)\])?',l)
		if not m: continue
		mod,names=m.group(1),m.group(2)
		if not mod.startswith('kern.'): continue
		if mod not in exports and mod!='kern.server': print(f"{f}:{i}: модуль {mod} не найден"); continue
		if names:
			for n in [x.strip() for x in names.split(',')]:
				if n not in exports[mod] and mod!='kern.test' and not n.startswith('*'): print(f"{f}:{i}: {mod} не экспортирует '{n}' (есть: {sorted(exports[mod])})")
