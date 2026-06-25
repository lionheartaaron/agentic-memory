// ClearScript exposes .NET members using their original PascalCase names.
// This bridge adapts them to the camelCase ts.LanguageServiceHost contract.

// .NET arrays come through ClearScript as proxy objects with .Length but no JS array methods.
// TypeScript calls .slice() on the file list, so we must convert to a native JS array here.
function dotnetArrayToJs(arr) {
    if (!arr) return [];
    var result = [];
    var len = arr.Length;
    for (var i = 0; i < len; i++) result.push(arr[i]);
    return result;
}

var tsHost = {
    getCompilationSettings:  function() {
        // Must return a plain JS object — TypeScript spreads/enumerates compiler options
        // internally (Object.assign, Object.keys) and gets nothing from a .NET proxy.
        var s = nativeHost.GetCompilationSettings();
        return {
            target: s.target, module: s.module, jsx: s.jsx, strict: s.strict,
            noEmit: s.noEmit, allowJs: s.allowJs,
            allowSyntheticDefaultImports: s.allowSyntheticDefaultImports,
            esModuleInterop: s.esModuleInterop, resolveJsonModule: s.resolveJsonModule,
            moduleResolution: s.moduleResolution
        };
    },
    getScriptFileNames:      function()         { return dotnetArrayToJs(nativeHost.GetScriptFileNames()); },
    getScriptVersion:        function(f)        { return nativeHost.GetScriptVersion(f); },
    getScriptSnapshot:       function(f)        {
        var snap = nativeHost.GetScriptSnapshot(f);
        if (!snap) return undefined;
        return {
            getText:        function(s,e) { return snap.GetText(s, e); },
            getLength:      function()    { return snap.GetLength(); },
            getChangeRange: function()    { return undefined; }
        };
    },
    getCurrentDirectory:     function()         { return nativeHost.GetCurrentDirectory(); },
    getDefaultLibFileName:   function(opts)     { return nativeHost.GetDefaultLibFileName(opts); },
    getScriptKind:           function(f)        { return nativeHost.GetScriptKind(f); },
    fileExists:              function(p)        { return nativeHost.FileExists(p); },
    readFile:                function(p,enc)    { return nativeHost.ReadFile(p, enc || null); },
    readDirectory:           function(p,x,e,i,d){ return dotnetArrayToJs(nativeHost.ReadDirectory(p,x,e,i,d||100)); },
    directoryExists:         function(p)        { return nativeHost.DirectoryExists(p); },
    getDirectories:          function(p)        { return dotnetArrayToJs(nativeHost.GetDirectories(p)); }
};

// Create the language service once — shared across all query calls for this project
var langService = ts.createLanguageService(tsHost, ts.createDocumentRegistry());

// ── getFileInfo: drives ExtractContextAsync ───────────────────────────────────
function getFileInfo(fileName) {
    var program = langService.getProgram();
    if (!program) return null;
    var sf = program.getSourceFile(fileName);
    if (!sf) return null;
    var tc = program.getTypeChecker();

    var symbols = [];
    var domainHints = [];
    var depth = 0;

    // ── Import partitioning ───────────────────────────────────────────────────
    // Framework noise: never useful to an AI reading context
    var REACT_NOISE = (function() {
        var s = {};
        ['useState','useEffect','useCallback','useMemo','useRef','useReducer','useContext',
         'useLayoutEffect','useImperativeHandle','useDebugValue','useId','useDeferredValue',
         'useTransition','useSyncExternalStore','Fragment','createContext','forwardRef','memo',
         'React','ReactNode','ReactElement','FC','PropsWithChildren','CSSProperties','HTMLAttributes',
         'useNavigate','useParams','useLocation','useSearchParams','useOutlet','useOutletContext',
         'useLoaderData','useActionData','Link','NavLink','Outlet',
         'useQueryClient','QueryClient','QueryClientProvider','useIsMutating','useIsFetching',
         'useQuery','useMutation','useInfiniteQuery'
        ].forEach(function(n) { s[n] = true; });
        return s;
    })();
    function isNoisyModule(mod) {
        return /lucide-react|@heroicons|react-icons|@radix-ui|@headlessui/.test(mod);
    }

    var localImports = {};  // relative module path → [names]
    var typeImports = [];   // type-only imports

    ts.forEachChild(sf, function(node) {
        if (!ts.isImportDeclaration(node)) return;
        var mod = node.moduleSpecifier.text;
        if (isNoisyModule(mod)) return;
        var isLocal = mod.charAt(0) === '.';
        var isTypeOnly = !!(node.importClause && node.importClause.isTypeOnly);
        var names = [];
        var clause = node.importClause;
        if (!clause) return;
        if (clause.name && !REACT_NOISE[clause.name.text]) names.push(clause.name.text);
        if (clause.namedBindings && ts.isNamedImports(clause.namedBindings)) {
            clause.namedBindings.elements.forEach(function(el) {
                if (!REACT_NOISE[el.name.text]) names.push(el.name.text);
            });
        }
        if (names.length === 0) return;
        // Type-only imports or imports from a types module go to typeImports
        if (isTypeOnly || mod === '../types' || mod === './types' || mod === '../../types' || mod.endsWith('/types')) {
            names.forEach(function(n) { typeImports.push(n); });
        } else if (isLocal) {
            if (!localImports[mod]) localImports[mod] = [];
            names.forEach(function(n) { localImports[mod].push(n); });
        }
    });

    ts.forEachChild(sf, function visit(node) {
        // Collect top-level declarations only (depth === 0 suppresses nested arrow fns)
        if (depth === 0 && (ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node) ||
            ts.isFunctionDeclaration(node) || ts.isVariableStatement(node) ||
            ts.isTypeAliasDeclaration(node) || ts.isEnumDeclaration(node))) {

            var sym = node.name ? tc.getSymbolAtLocation(node.name) : null;
            var name = node.name ? node.name.text : '(default)';
            var kind = ts.SyntaxKind[node.kind];
            var type = sym ? tc.typeToString(tc.getTypeOfSymbolAtLocation(sym, node)) : null;

            symbols.push({ name: name, kind: kind, type: type, line: sf.getLineAndCharacterOfPosition(node.pos).line + 1 });
        }

        // useState — detected at VariableDeclaration to capture binding name
        if (ts.isVariableDeclaration(node) && node.initializer &&
            ts.isCallExpression(node.initializer) &&
            ts.isIdentifier(node.initializer.expression) &&
            node.initializer.expression.text === 'useState') {
            var call = node.initializer;
            var stateTypeArgs = call.typeArguments;
            var stateType = (stateTypeArgs && stateTypeArgs.length > 0) ? stateTypeArgs[0].getText(sf) : null;
            var stateName = null;
            if (node.name && ts.isArrayBindingPattern(node.name) && node.name.elements.length > 0) {
                var firstEl = node.name.elements[0];
                if (firstEl && ts.isBindingElement(firstEl) && firstEl.name && ts.isIdentifier(firstEl.name)) {
                    stateName = firstEl.name.text;
                }
            }
            // Infer type from initial value when no explicit type argument
            if (!stateType && call.arguments.length > 0) {
                var initVal = call.arguments[0];
                if (ts.isObjectLiteralExpression(initVal)) stateType = '{}';
                else if (initVal.kind === ts.SyntaxKind.FalseKeyword || initVal.kind === ts.SyntaxKind.TrueKeyword) stateType = 'boolean';
                else if (initVal.kind === ts.SyntaxKind.NullKeyword) stateType = 'null';
                else if (ts.isStringLiteral(initVal)) stateType = 'string';
                else if (ts.isNumericLiteral(initVal)) stateType = 'number';
                else if (ts.isArrayLiteralExpression(initVal)) stateType = '[]';
            }
            domainHints.push({ kind: 'state', name: stateName, type: stateType || 'unknown' });
        }

        // useParams — detected at VariableDeclaration to capture destructured route param names
        if (ts.isVariableDeclaration(node) && node.initializer &&
            ts.isCallExpression(node.initializer) &&
            ts.isIdentifier(node.initializer.expression) &&
            node.initializer.expression.text === 'useParams') {
            if (node.name && ts.isObjectBindingPattern(node.name)) {
                node.name.elements.forEach(function(el) {
                    if (el.name && ts.isIdentifier(el.name)) {
                        domainHints.push({ kind: 'route-param', name: el.name.text });
                    }
                });
            }
        }

        // navigate('/path') — literal navigation targets
        if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) &&
            node.expression.text === 'navigate' && node.arguments.length > 0 &&
            ts.isStringLiteral(node.arguments[0])) {
            domainHints.push({ kind: 'navigate-to', path: node.arguments[0].text });
        }

        // <Link to="..."> — static Link destinations
        if (ts.isJsxAttribute(node) && node.name && ts.isIdentifier(node.name) &&
            node.name.text === 'to' && node.initializer) {
            var linkPath = null;
            if (ts.isStringLiteral(node.initializer)) {
                linkPath = node.initializer.text;
            } else if (ts.isJsxExpression(node.initializer) && node.initializer.expression) {
                var expr = node.initializer.expression;
                if (ts.isStringLiteral(expr)) {
                    linkPath = expr.text;
                } else if (ts.isTemplateExpression(expr)) {
                    linkPath = expr.head.text + ':id';
                } else if (ts.isNoSubstitutionTemplateLiteral && ts.isNoSubstitutionTemplateLiteral(expr)) {
                    linkPath = expr.text;
                }
            }
            if (linkPath) domainHints.push({ kind: 'link-to', path: linkPath });
        }

        // fetch() calls with HTTP method detection
        if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'fetch') {
            var urlArg = node.arguments[0];
            var optArg = node.arguments[1];
            var fetchMethod = 'GET';
            if (optArg && ts.isObjectLiteralExpression(optArg)) {
                var methodProp = optArg.properties.find(function(p) { return p.name && p.name.text === 'method'; });
                if (methodProp && methodProp.initializer && ts.isStringLiteral(methodProp.initializer)) {
                    fetchMethod = methodProp.initializer.text.toUpperCase();
                }
            }
            if (urlArg && ts.isStringLiteral(urlArg)) {
                domainHints.push({ kind: 'endpoint', method: fetchMethod, url: urlArg.text });
            }
        }

        // AbortController
        if (ts.isNewExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'AbortController') {
            domainHints.push({ kind: 'abortable' });
        }

        // EventSource / SSE
        if (ts.isNewExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'EventSource') {
            domainHints.push({ kind: 'sse-source' });
        }

        // ReadableStream SSE (res.body.getReader() fetch+stream pattern)
        if (ts.isCallExpression(node) &&
            ts.isPropertyAccessExpression(node.expression) &&
            node.expression.name.text === 'getReader') {
            domainHints.push({ kind: 'streams-sse' });
        }

        // useQuery — full queryKey array + queryFn call expression
        if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'useQuery') {
            var uqArg = node.arguments[0];
            if (uqArg && ts.isObjectLiteralExpression(uqArg)) {
                var queryKey = null;
                var queryFn = null;
                uqArg.properties.forEach(function(p) {
                    if (!p.name) return;
                    var propName = ts.isIdentifier(p.name) ? p.name.text : null;
                    if (propName === 'queryKey' && p.initializer && ts.isArrayLiteralExpression(p.initializer)) {
                        queryKey = '[' + p.initializer.elements.map(function(el) {
                            if (ts.isStringLiteral(el)) return '"' + el.text + '"';
                            if (ts.isIdentifier(el)) return el.text;
                            return '?';
                        }).join(', ') + ']';
                    }
                    if (propName === 'queryFn' && p.initializer && ts.isArrowFunction(p.initializer)) {
                        var body = p.initializer.body;
                        if (ts.isCallExpression(body)) {
                            var callee = body.expression.getText ? body.expression.getText(sf) : null;
                            if (callee) {
                                var callArgs = body.arguments.map(function(a) { return a.getText ? a.getText(sf) : '?'; }).join(', ');
                                queryFn = callee + '(' + callArgs + ')';
                            }
                        }
                    }
                });
                domainHints.push({ kind: 'query', key: queryKey, fn: queryFn });
            }
        }

        // useMutation — mutationFn call expression + onSuccess side-effects (invalidates, navigate)
        if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'useMutation') {
            var umArg = node.arguments[0];
            var mutFn = null;
            var mutInvalidates = [];
            var mutNavigatesTo = null;
            if (umArg && ts.isObjectLiteralExpression(umArg)) {
                umArg.properties.forEach(function(p) {
                    if (!p.name) return;
                    var propName = ts.isIdentifier(p.name) ? p.name.text : null;
                    if (propName === 'mutationFn' && p.initializer && ts.isArrowFunction(p.initializer)) {
                        var body = p.initializer.body;
                        if (ts.isCallExpression(body)) {
                            var callee = body.expression.getText ? body.expression.getText(sf) : null;
                            if (callee) {
                                var callArgs = body.arguments.map(function(a) { return a.getText ? a.getText(sf) : '?'; }).join(', ');
                                mutFn = callee + '(' + callArgs + ')';
                            }
                        }
                    }
                    if (propName === 'onSuccess' && p.initializer && ts.isArrowFunction(p.initializer)) {
                        (function walkSuccess(n) {
                            if (ts.isCallExpression(n)) {
                                if (ts.isPropertyAccessExpression(n.expression) &&
                                    n.expression.name.text === 'invalidateQueries' &&
                                    n.arguments.length > 0) {
                                    var qArg = n.arguments[0];
                                    if (qArg && ts.isObjectLiteralExpression(qArg)) {
                                        qArg.properties.forEach(function(pp) {
                                            if (pp.name && ts.isIdentifier(pp.name) && pp.name.text === 'queryKey' &&
                                                pp.initializer && ts.isArrayLiteralExpression(pp.initializer) &&
                                                pp.initializer.elements.length > 0) {
                                                var fst = pp.initializer.elements[0];
                                                if (fst && ts.isStringLiteral(fst)) mutInvalidates.push(fst.text);
                                            }
                                        });
                                    }
                                }
                                if (ts.isIdentifier(n.expression) && n.expression.text === 'navigate' &&
                                    n.arguments.length > 0 && ts.isStringLiteral(n.arguments[0])) {
                                    mutNavigatesTo = n.arguments[0].text;
                                }
                            }
                            ts.forEachChild(n, walkSuccess);
                        })(p.initializer.body);
                    }
                });
            }
            domainHints.push({ kind: 'mutation', fn: mutFn, invalidates: mutInvalidates, navigatesTo: mutNavigatesTo });
        }

        depth++;
        ts.forEachChild(node, visit);
        depth--;
    });

    // Semantic diagnostics (§3.3: from typeChecker, not just syntax errors)
    var diagsRaw = langService.getSemanticDiagnostics(fileName);
    var diags = diagsRaw.slice(0, 10).map(function(d) {
        return {
            severity: d.category,  // 0=Warning, 1=Error, 2=Message, 3=Suggestion
            code: 'TS' + d.code,
            message: typeof d.messageText === 'string' ? d.messageText : d.messageText.messageText,
            line: d.file ? d.file.getLineAndCharacterOfPosition(d.start || 0).line + 1 : 0
        };
    });

    return { fileName: fileName, symbols: symbols, domainHints: domainHints, diagnostics: diags, localImports: localImports, typeImports: typeImports };
}

// ── getSymbols ────────────────────────────────────────────────────────────────
function getSymbols(fileName) {
    var program = langService.getProgram();
    if (!program) return [];
    var sf = program.getSourceFile(fileName);
    if (!sf) return [];
    var tc = program.getTypeChecker();
    var results = [];

    ts.forEachChild(sf, function(node) {
        if (!node.name) return;
        var sym = tc.getSymbolAtLocation(node.name);
        if (!sym) return;
        var type = tc.typeToString(tc.getTypeOfSymbolAtLocation(sym, node));
        var flags = ts.getCombinedModifierFlags(node);
        var access = (flags & 4) ? 'private' : (flags & 16) ? 'protected' : 'public';
        results.push({
            name: node.name.text,
            kind: ts.SyntaxKind[node.kind],
            type: type,
            accessibility: access,
            line: sf.getLineAndCharacterOfPosition(node.pos).line + 1
        });
    });
    return results;
}

// ── findReferences ────────────────────────────────────────────────────────────
function findReferences(fileName, symbolName) {
    var program = langService.getProgram();
    if (!program) return [];
    var sf = program.getSourceFile(fileName);
    if (!sf) return [];

    // Find the position of the first occurrence of symbolName in the file
    var text = sf.getFullText();
    var idx = text.indexOf(symbolName);
    if (idx < 0) return [];

    var refs = langService.findReferences(fileName, idx);
    if (!refs) return [];

    var results = [];
    refs.forEach(function(refGroup) {
        refGroup.references.forEach(function(ref) {
            var refSf = program.getSourceFile(ref.fileName);
            var line = refSf ? refSf.getLineAndCharacterOfPosition(ref.textSpan.start).line + 1 : 0;
            results.push({ filePath: ref.fileName, line: line, context: symbolName });
        });
    });
    return results;
}

// ── getDiagnostics ────────────────────────────────────────────────────────────
function getDiagnostics(fileName) {
    var diags = langService.getSemanticDiagnostics(fileName);
    return diags.slice(0, 20).map(function(d) {
        return {
            severity: d.category === 1 ? 'Error' : d.category === 0 ? 'Warning' : 'Info',
            code: 'TS' + d.code,
            message: typeof d.messageText === 'string' ? d.messageText : d.messageText.messageText,
            line: d.file ? d.file.getLineAndCharacterOfPosition(d.start || 0).line + 1 : 0
        };
    });
}
