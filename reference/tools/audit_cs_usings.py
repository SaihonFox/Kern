import re,glob,os,collections
# namespace -> имена типов, объявленные в проекте
types=collections.defaultdict(set); fileinfo={}
for f in glob.glob('src/**/*.cs',recursive=True):
	if '/obj/' in f or '/bin/' in f: continue
	t=open(f,encoding='utf-8').read()
	m=re.search(r'^namespace ([\w.]+)',t,re.M); ns=m.group(1) if m else ''
	fileinfo[f]=(ns,t)
	for d in re.finditer(r'^\s*(?:public |internal |private |protected )?(?:static |sealed |abstract |readonly |partial |unsafe )*(?:class|struct|record|interface|enum)(?: struct| class)? (\w+)',t,re.M):
		types[d.group(1)].add(ns)
# глобальные using (Directory.Build.props / ImplicitUsings)
implicit={'System','System.IO','System.Linq','System.Collections.Generic','System.Net.Http','System.Threading','System.Threading.Tasks'}
problems=0
for f,(ns,t) in sorted(fileinfo.items()):
	usings=set(re.findall(r'^using (?:static )?(?:\w+ = )?([\w.]+);',t,re.M))|implicit
	body=re.sub(r'//.*|"(?:\\.|[^"\\])*"','',t)
	body=re.sub(r'^using .*$','',body,flags=re.M)
	for name,nss in types.items():
		if ns in nss or any(n==ns or ns.startswith(n+'.') for n in nss) and False: continue
		if len(name)<3: continue
		if not re.search(r'(?<![\w.])'+re.escape(name)+r'(?![\w])',body): continue
		# доступен, если: объявлен в своём namespace/родительском, либо есть using
		ok=False
		for n in nss:
			if n==ns or ns.startswith(n+'.') or n in usings: ok=True
		if not ok:
			# возможно объявлено тут же вложенным/локально
			if re.search(r'(class|struct|record|enum|interface) '+name+r'\b',t): continue
			print(f"{f}: '{name}' нужен using {sorted(nss)}"); problems+=1
print("проблем:",problems)
