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
            moduleResolution: s.moduleResolution,
            skipLibCheck: s.skipLibCheck, skipDefaultLibCheck: s.skipDefaultLibCheck
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
// Covers: class, interface, function, enum, type alias, AND export const/let/var
// (arrow components, hooks, api functions).  Accessibility is 'exported' for
// anything with the export modifier (what ReferenceIndexWorker filters on), and
// 'public' otherwise — matching the contract expected by the C# side.
//
// Kind names are normalised to short friendly names ('Function', 'Variable',
// 'Class', …) so they align with the C# Roslyn provider's naming and the
// SymbolsIndex kind filter in the dashboard.
// Canonical lowercase kinds, aligned with the C# Roslyn provider (and the dashboard's lowercase
// 'method'/'function'/'constructor' expectations) so an agent sees ONE vocabulary across languages.
var _KIND_MAP = {
    ClassDeclaration:     'class',
    InterfaceDeclaration: 'interface',
    FunctionDeclaration:  'function',
    EnumDeclaration:      'enum',
    TypeAliasDeclaration: 'type-alias',
    VariableDeclaration:  'variable',
    ModuleDeclaration:    'namespace',
};
function toFriendlyKind(syntaxKindName) {
    return _KIND_MAP[syntaxKindName] || syntaxKindName.toLowerCase();
}

// ── P1 Tier 0 helpers: structured symbol shape (camelCase keys map onto the C# SymbolInfo) ────

// Modifier keyword texts (static/abstract/async/readonly/export/...). Decorators (kind 'Decorator')
// are skipped because their SyntaxKind name does not end in 'Keyword'.
function tsModifiers(node) {
    var out = [];
    if (node && node.modifiers) {
        for (var i = 0; i < node.modifiers.length; i++) {
            var kn = ts.SyntaxKind[node.modifiers[i].kind];
            if (kn && kn.length > 7 && kn.lastIndexOf('Keyword') === kn.length - 7)
                out.push(kn.slice(0, -7).toLowerCase());
        }
    }
    return out;
}

function endLineOf(node, sf) {
    return sf.getLineAndCharacterOfPosition(node.getEnd()).line + 1;
}

// P5: name of a declaration node for caller attribution (null if the node is not a named decl).
function tsDeclName(node, sf) {
    if (ts.isFunctionDeclaration(node) || ts.isMethodDeclaration(node) ||
        ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node) ||
        ts.isGetAccessorDeclaration(node) || ts.isSetAccessorDeclaration(node)) {
        return node.name ? node.name.getText(sf) : null;
    }
    if (ts.isConstructorDeclaration(node)) return 'constructor';
    // const foo = () => {} / const foo = function() {}
    if ((ts.isArrowFunction(node) || ts.isFunctionExpression(node)) &&
        node.parent && ts.isVariableDeclaration(node.parent) && node.parent.name) {
        return node.parent.name.getText(sf);
    }
    return null;
}

// True when this identifier IS the name of a declaration (not a usage). The C# index never records
// declaration sites because a declaration name is a token, not an IdentifierNameSyntax — TS must
// skip them explicitly or every symbol gains a spurious self-reference.
function tsIsDeclarationName(node) {
    var p = node.parent;
    if (!p || p.name !== node) return false;
    return ts.isClassDeclaration(p) || ts.isInterfaceDeclaration(p) || ts.isFunctionDeclaration(p) ||
           ts.isEnumDeclaration(p) || ts.isEnumMember(p) || ts.isTypeAliasDeclaration(p) ||
           ts.isVariableDeclaration(p) || ts.isParameter(p) ||
           ts.isMethodDeclaration(p) || ts.isMethodSignature(p) ||
           ts.isPropertyDeclaration(p) || ts.isPropertySignature(p) ||
           ts.isGetAccessorDeclaration(p) || ts.isSetAccessorDeclaration(p);
}

// P2: usage-kind label for a reference identifier (mirrors C# ProjectIndex.ClassifyRole).
function tsRefRole(node) {
    var p = node.parent;
    if (!p) return 'read';
    if (ts.isCallExpression(p) && p.expression === node) return 'call';
    if (ts.isPropertyAccessExpression(p) && p.name === node && p.parent && ts.isCallExpression(p.parent)) return 'call';
    if (ts.isNewExpression(p) && p.expression === node) return 'new';
    if (ts.isTypeReferenceNode && ts.isTypeReferenceNode(p)) return 'typeref';
    if (ts.isHeritageClause && p.parent && ts.isHeritageClause(p.parent)) return 'implements';
    if (ts.isBinaryExpression(p) && p.left === node &&
        p.operatorToken && p.operatorToken.kind === ts.SyntaxKind.EqualsToken) return 'write';
    return 'read';
}

// P2: JSDoc summary + @deprecated tag for a declaration (node.jsDoc is present on documented nodes).
function tsDoc(node) {
    var summary = null, deprecated = false;
    if (node.jsDoc && node.jsDoc.length) {
        var last = node.jsDoc[node.jsDoc.length - 1];
        if (last && typeof last.comment === 'string') summary = last.comment;
    }
    if (ts.getJSDocTags) {
        var tags = ts.getJSDocTags(node) || [];
        for (var i = 0; i < tags.length; i++) {
            if (tags[i].tagName && tags[i].tagName.text === 'deprecated') deprecated = true;
        }
    }
    return { summary: summary, deprecated: deprecated };
}

// Parameters from a function-like declaration's syntactic parameter list. Declared type
// annotations are used directly; un-annotated params fall back to the checker's inferred type.
function tsParams(fnNode, sf, tc) {
    if (!fnNode || !fnNode.parameters) return [];
    var out = [];
    for (var i = 0; i < fnNode.parameters.length; i++) {
        var p = fnNode.parameters[i];
        var pType;
        if (p.type) {
            pType = p.type.getText(sf);
        } else {
            try { pType = tc.typeToString(tc.getTypeAtLocation(p)); } catch (e) { pType = 'any'; }
        }
        out.push({
            name: p.name ? p.name.getText(sf) : ('arg' + i),
            type: pType,
            ordinal: i,
            isOptional: !!p.questionToken || !!p.initializer,
            defaultValue: p.initializer ? p.initializer.getText(sf) : null,
            refKind: 'none',
            isParams: !!p.dotDotDotToken,
            nullableAnnotation: 'none',
            attributes: []
        });
    }
    return out;
}

// P4: declared generic type parameters with constraint text.
function tsTypeParams(node, sf) {
    if (!node.typeParameters) return [];
    var out = [];
    for (var i = 0; i < node.typeParameters.length; i++) {
        var tp = node.typeParameters[i];
        var constraints = [];
        if (tp.constraint) constraints.push(tp.constraint.getText(sf));
        out.push({ name: tp.name ? tp.name.getText(sf) : ('T' + i), constraints: constraints, variance: null });
    }
    return out;
}

function tsEnumMembers(enumNode, sf) {
    if (!enumNode || !enumNode.members) return [];
    return enumNode.members.map(function(m) {
        var isNumeric = m.initializer && ts.isNumericLiteral(m.initializer);
        return {
            name: m.name ? m.name.getText(sf) : '',
            value: isNumeric ? Number(m.initializer.text) : null,
            explicitExpression: (m.initializer && !isNumeric) ? m.initializer.getText(sf) : null
        };
    });
}

// Return type of a function-like declaration. The C# provider reports a method's return type (not
// the whole signature) as Type; match that so the two languages agree.
function tsReturnType(node, sf, tc) {
    try {
        var sig = tc.getSignatureFromDeclaration(node);
        if (sig) return tc.typeToString(tc.getReturnTypeOfSignature(sig));
    } catch (e) {}
    return null;
}

// Unwrap Promise<T> → T (the TS analog of the C# provider's Task<T>/ValueTask<T> unwrap), so an
// async function's "real" return type is available in one place across both languages.
function tsUnwrapPromise(typeStr) {
    if (!typeStr) return null;
    var m = /^Promise<([\s\S]+)>$/.exec(typeStr);
    return m ? m[1] : null;
}

function tsMemberKind(m) {
    if (ts.isMethodDeclaration(m) || ts.isMethodSignature(m)) return 'method';
    if (ts.isGetAccessorDeclaration(m) || ts.isSetAccessorDeclaration(m)) return 'property';
    if (ts.isPropertyDeclaration(m) || ts.isPropertySignature(m)) return 'property';
    if (ts.isConstructorDeclaration(m)) return 'constructor';
    return null;
}

// Emit method / property / constructor symbols for a class or interface body. The previous
// non-recursive top-level walk dropped every member, so an agent saw a class with no methods.
function tsEmitMembers(typeNode, typeName, sf, tc, out) {
    if (!typeNode.members) return;
    for (var i = 0; i < typeNode.members.length; i++) {
        var m = typeNode.members[i];
        var mk = tsMemberKind(m);
        if (!mk) continue;
        var mname = mk === 'constructor' ? 'constructor' : (m.name ? m.name.getText(sf) : null);
        if (!mname) continue;
        var mmods = tsModifiers(m);
        var maccess = mmods.indexOf('private') >= 0 ? 'private'
                    : mmods.indexOf('protected') >= 0 ? 'protected' : 'public';
        var isFnLike = (mk === 'method' || mk === 'constructor');
        var mtype = null;
        var mUnwrap = null;
        if (isFnLike) {
            mtype = tsReturnType(m, sf, tc);
            mUnwrap = tsUnwrapPromise(mtype);
        } else if (m.name) {
            try {
                var msym = tc.getSymbolAtLocation(m.name);
                if (msym) mtype = tc.typeToString(tc.getTypeOfSymbolAtLocation(msym, m));
            } catch (e) {}
        }
        var mdoc = tsDoc(m);
        var anchor = m.name || m;
        out.push({
            name: mname,
            kind: mk,
            type: mtype,
            accessibility: maccess,
            line: sf.getLineAndCharacterOfPosition(anchor.getStart()).line + 1,
            endLine: endLineOf(m, sf),
            modifiers: mmods,
            isStatic: mmods.indexOf('static') >= 0,
            isAbstract: mmods.indexOf('abstract') >= 0,
            isAsync: mmods.indexOf('async') >= 0,
            isAwaitable: !!mUnwrap || mmods.indexOf('async') >= 0,
            returnTypeUnwrapped: mUnwrap,
            parameters: isFnLike ? tsParams(m, sf, tc) : [],
            typeParameters: mk === 'method' ? tsTypeParams(m, sf) : [],
            docSummary: mdoc.summary,
            isDeprecated: mdoc.deprecated,
            containingTypeFullName: typeName
        });
    }
}

function getSymbols(fileName) {
    var program = langService.getProgram();
    if (!program) return [];
    var sf = program.getSourceFile(fileName);
    if (!sf) return [];
    var tc = program.getTypeChecker();
    var results = [];

    function processNode(node) {
        // Check modifiers directly — getCombinedModifierFlags can return 0 for exported
        // nodes in some TypeScript 5.x builds running inside ClearScript/V8.
        var modifiers = tsModifiers(node);
        // Exported = the external API surface → 'public'; module-scoped → 'internal'. Same vocabulary
        // the C# provider uses, so accessibility means the same thing across languages.
        var accessibility = modifiers.indexOf('export') >= 0 ? 'public' : 'internal';
        var doc = tsDoc(node);

        // VariableStatement: export const Foo = () => {} / export const api = { ... } / destructured.
        if (ts.isVariableStatement(node)) {
            node.declarationList.declarations.forEach(function(decl) {
                if (!decl.name) return;
                if (!ts.isIdentifier(decl.name)) {
                    // Destructured export: export const { a, b } = ... / export const [x] = ...
                    var elems = (ts.isObjectBindingPattern(decl.name) || ts.isArrayBindingPattern(decl.name))
                        ? decl.name.elements : [];
                    elems.forEach(function(el) {
                        if (!el.name || !ts.isIdentifier(el.name)) return;
                        var esym = tc.getSymbolAtLocation(el.name);
                        results.push({
                            name: el.name.text, kind: 'variable',
                            type: esym ? tc.typeToString(tc.getTypeOfSymbolAtLocation(esym, el)) : null,
                            accessibility: accessibility,
                            line: sf.getLineAndCharacterOfPosition(el.name.getStart()).line + 1,
                            endLine: endLineOf(el, sf), modifiers: modifiers,
                            docSummary: doc.summary, isDeprecated: doc.deprecated
                        });
                    });
                    return;
                }
                var sym = tc.getSymbolAtLocation(decl.name);
                if (!sym) return;
                var fn = (decl.initializer &&
                    (ts.isArrowFunction(decl.initializer) || ts.isFunctionExpression(decl.initializer)))
                    ? decl.initializer : null;
                var type = fn ? tsReturnType(fn, sf, tc)
                              : tc.typeToString(tc.getTypeOfSymbolAtLocation(sym, decl));
                var vUnwrap = fn ? tsUnwrapPromise(type) : null;
                results.push({
                    name: decl.name.text,
                    kind: fn ? 'function' : 'variable',
                    type: type,
                    accessibility: accessibility,
                    line: sf.getLineAndCharacterOfPosition(decl.name.getStart()).line + 1,
                    endLine: endLineOf(decl, sf),
                    modifiers: modifiers,
                    isStatic: modifiers.indexOf('static') >= 0,
                    isAbstract: modifiers.indexOf('abstract') >= 0,
                    isAsync: fn ? (tsModifiers(fn).indexOf('async') >= 0) : false,
                    isAwaitable: fn ? (!!vUnwrap || tsModifiers(fn).indexOf('async') >= 0) : false,
                    returnTypeUnwrapped: vUnwrap,
                    parameters: fn ? tsParams(fn, sf, tc) : [],
                    typeParameters: fn ? tsTypeParams(fn, sf) : [],
                    docSummary: doc.summary,
                    isDeprecated: doc.deprecated
                });
            });
            return;
        }

        // Named declaration, or an anonymous `export default function/class` (synthetic name "default").
        var declName = (node.name && ts.isIdentifier(node.name)) ? node.name.text
            : (modifiers.indexOf('default') >= 0 &&
               (ts.isFunctionDeclaration(node) || ts.isClassDeclaration(node)) ? 'default' : null);
        if (declName === null) return;

        var isFn = ts.isFunctionDeclaration(node);
        var nsym = node.name ? tc.getSymbolAtLocation(node.name) : null;
        var ntype = isFn ? tsReturnType(node, sf, tc)
                         : (nsym ? tc.typeToString(tc.getTypeOfSymbolAtLocation(nsym, node)) : null);
        var nUnwrap = isFn ? tsUnwrapPromise(ntype) : null;
        results.push({
            name: declName,
            kind: toFriendlyKind(ts.SyntaxKind[node.kind]),
            type: ntype,
            accessibility: accessibility,
            line: sf.getLineAndCharacterOfPosition((node.name || node).getStart()).line + 1,
            endLine: endLineOf(node, sf),
            modifiers: modifiers,
            isStatic: modifiers.indexOf('static') >= 0,
            isAbstract: modifiers.indexOf('abstract') >= 0,
            isAsync: modifiers.indexOf('async') >= 0,
            isAwaitable: isFn && (!!nUnwrap || modifiers.indexOf('async') >= 0),
            returnTypeUnwrapped: nUnwrap,
            parameters: isFn ? tsParams(node, sf, tc) : [],
            typeParameters: tsTypeParams(node, sf),
            enumMembers: ts.isEnumDeclaration(node) ? tsEnumMembers(node, sf) : [],
            docSummary: doc.summary,
            isDeprecated: doc.deprecated
        });

        // Recurse into class / interface bodies so members are surfaced (with their containing type).
        if (ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node)) {
            tsEmitMembers(node, declName, sf, tc, results);
        }
        // Namespace members: recurse into the module body.
        if (ts.isModuleDeclaration(node) && node.body && node.body.statements) {
            ts.forEachChild(node.body, processNode);
        }
    }

    ts.forEachChild(sf, processNode);
    return results;
}

// ── Inverted reference index (mirrors C# ProjectIndex.BuildReferenceIndex) ────
// Built once per program version; subsequent lookups are O(1) dictionary reads.
// This is what makes per-file reference analysis fast enough to stay within the
// 45 s job timeout even on large TypeScript projects.

var _refIndex = null;        // symbolName → [{filePath, line, context}]
var _refIndexVersion = -1;   // program stamp — rebuild when a file is invalidated
var _declFiles = null;       // name → [filePaths] of EVERY top-level declaration (collision diagnostics)

function buildReferenceIndex() {
    var program = langService.getProgram();
    if (!program) { _declFiles = {}; return {}; }
    var checker = program.getTypeChecker();

    // Collect source files, skipping .d.ts declaration files and node_modules.
    var sourceFilesRaw = program.getSourceFiles();
    var sources = [];
    for (var i = 0; i < sourceFilesRaw.length; i++) {
        var sf = sourceFilesRaw[i];
        if (!sf.isDeclarationFile && sf.fileName.indexOf('/node_modules/') < 0)
            sources.push(sf);
    }

    // Pass 1: collect top-level exported declarations, mapping name → ts.Symbol.
    // Mirrors ProjectIndex Pass 1 (declaredSymbols dictionary).  First declaration
    // per name wins — same policy as Roslyn's BuildReferenceIndex.
    var declSymbols = {};   // name → ts.Symbol of the canonical declaration
    var declaredNames = {}; // name → true  (fast pre-filter before checker call)
    var defaultExportByNormPath = {}; // normalised file path → name of its default export

    for (var si = 0; si < sources.length; si++) {
        var sf0 = sources[si];
        function addDecl(nameNode) {
            if (!nameNode || !ts.isIdentifier(nameNode)) return;
            var name = nameNode.text;
            if (declaredNames[name]) return; // first wins
            var sym = checker.getSymbolAtLocation(nameNode);
            if (!sym) return;
            declSymbols[name]  = sym;
            declaredNames[name] = true;
        }
        var collectDecls = function(node) {
            // Record the file's default export name so default imports (`import App from './App'`)
            // resolve syntactically in Pass 3 when the type checker is degraded (missing lib/types).
            var nmods = tsModifiers(node);
            if (nmods.indexOf('export') >= 0 && nmods.indexOf('default') >= 0 &&
                node.name && ts.isIdentifier(node.name)) {
                defaultExportByNormPath[_normPath(sf0.fileName)] = node.name.text;
            } else if (ts.isExportAssignment(node) && !node.isExportEquals &&
                node.expression && ts.isIdentifier(node.expression)) {
                defaultExportByNormPath[_normPath(sf0.fileName)] = node.expression.text;
            }
            if (ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node)) {
                addDecl(node.name);
                // Member-level declarations too, so `obj.method()` / `obj.prop` references are tracked
                // (parity with the C# provider, which indexes every member).
                if (node.members) node.members.forEach(function(m) {
                    if (m.name && ts.isIdentifier(m.name) && tsMemberKind(m)) addDecl(m.name);
                });
            } else if (ts.isFunctionDeclaration(node) || ts.isEnumDeclaration(node) ||
                ts.isTypeAliasDeclaration(node)) {
                addDecl(node.name);
            } else if (ts.isVariableStatement(node)) {
                node.declarationList.declarations.forEach(function(d) {
                    if (ts.isIdentifier(d.name)) addDecl(d.name);
                    else if (ts.isObjectBindingPattern(d.name) || ts.isArrayBindingPattern(d.name))
                        d.name.elements.forEach(function(el) { if (el.name) addDecl(el.name); }); // destructured export
                });
            } else if (ts.isModuleDeclaration(node) && node.body && node.body.statements) {
                ts.forEachChild(node.body, collectDecls); // namespace members
            }
        };
        ts.forEachChild(sf0, collectDecls);
    }

    // Pass 2: single traversal of all files; for every identifier whose text matches a
    // declared name, resolve via the type checker and check symbol identity — same as
    // Roslyn's SymbolEqualityComparer.Default.Equals.
    // ts.SymbolFlags.Alias = 2097152; follow through barrel re-exports (export * from './x').
    var index = {};
    for (var si2 = 0; si2 < sources.length; si2++) {
        var sf2 = sources[si2];
        var encStack = [];   // P5: stack of enclosing named declarations (caller attribution)
        (function visit(node) {
            var dn = tsDeclName(node, sf2);
            if (dn) encStack.push(dn);

            if (ts.isIdentifier(node) && declaredNames[node.text] && !tsIsDeclarationName(node)) {
                var sym = checker.getSymbolAtLocation(node);
                if (sym) {
                    var isAlias = (sym.flags & 2097152) !== 0;
                    var resolved = isAlias ? checker.getAliasedSymbol(sym) : sym;
                    var name = node.text;
                    var canonical = declSymbols[name];
                    // Match by symbol identity; by shared declaration node (instantiated generic
                    // members — the analog of Roslyn's OriginalDefinition); or, for an imported alias of
                    // a project-declared name, by the name. getAliasedSymbol frequently yields a
                    // declaration-LESS export symbol cross-file (no node/name to compare), so the
                    // `declaredNames` pre-filter is the guard: `name` is always a project declaration.
                    // The rare miss (a renamed external import colliding with a project name) errs
                    // toward "used" — the safe direction for orphan detection.
                    var matches = !!canonical && (
                        resolved === canonical ||
                        (resolved && resolved.declarations && canonical.declarations &&
                         resolved.declarations.length && canonical.declarations.length &&
                         resolved.declarations[0] === canonical.declarations[0]) ||
                        isAlias);
                    if (matches) {
                        var lc = sf2.getLineAndCharacterOfPosition(node.getStart());
                        if (!index[name]) index[name] = [];
                        index[name].push({
                            filePath: sf2.fileName, line: lc.line + 1, context: name,
                            role: tsRefRole(node),
                            enclosingName: encStack.length ? encStack[encStack.length - 1] : null
                        });
                    }
                }
            }
            ts.forEachChild(node, visit);
            if (dn) encStack.pop();
        })(sf2);
    }
    // Pass 3: import-declaration fallback.
    // When checker.getSymbolAtLocation returns null for type-only import specifiers
    // (e.g. because lib.d.ts is missing and cascading errors impair type resolution),
    // Pass 2 records nothing.  This pass detects relative imports of declared names by
    // resolving the module specifier string to a file path and checking it against the
    // symbol's declared source file — no type checker required.
    for (var si3 = 0; si3 < sources.length; si3++) {
        var sf3 = sources[si3];
        var dir3 = sf3.fileName.replace(/\/[^\/]+$/, ''); // dirname
        ts.forEachChild(sf3, function(iNode) {
            if (!ts.isImportDeclaration(iNode)) return;
            var modSpecNode = iNode.moduleSpecifier;
            if (!ts.isStringLiteral(modSpecNode)) return;
            var modText = modSpecNode.text;
            if (modText.charAt(0) !== '.') return; // only relative imports

            var clause = iNode.importClause;
            if (!clause) return;

            // Candidate absolute paths the module specifier could resolve to
            var base = _joinPath(dir3, modText);
            var candidates = [
                base + '.ts', base + '.tsx',
                base + '/index.ts', base + '/index.tsx'
            ];
            var importLine = sf3.getLineAndCharacterOfPosition(iNode.getStart()).line + 1;

            function recordImport(name) {
                if (!name || !declaredNames[name]) return;
                if (index[name] && index[name].some(function(r) { return r.filePath === sf3.fileName; })) return;
                if (!index[name]) index[name] = [];
                index[name].push({ filePath: sf3.fileName, line: importLine, context: name, role: 'import' });
            }

            // Named imports — match each name against the file that declares it.
            if (clause.namedBindings && ts.isNamedImports(clause.namedBindings)) {
                clause.namedBindings.elements.forEach(function(el) {
                    var name = el.name.text;
                    if (!declaredNames[name]) return;
                    var sym = declSymbols[name];
                    if (!sym || !sym.declarations || !sym.declarations.length) return;
                    var declSf = sym.declarations[0].getSourceFile();
                    if (!declSf) return;
                    var normDecl = _normPath(declSf.fileName);
                    for (var ci = 0; ci < candidates.length; ci++) {
                        if (_normPath(candidates[ci]) === normDecl) { recordImport(name); break; }
                    }
                });
            }

            // Default import (`import App from './App'`): resolve the module to the file's default
            // export — the local binding name is irrelevant. Without this, every default-exported
            // React component (App, the pages, Layout, …) is a false orphan whenever the checker is
            // degraded (no node_modules types) and Pass 2 resolves nothing.
            if (clause.name && ts.isIdentifier(clause.name)) {
                for (var di = 0; di < candidates.length; di++) {
                    var defName = defaultExportByNormPath[_normPath(candidates[di])];
                    if (defName) { recordImport(defName); break; }
                }
            }
        });
    }

    return index;
}

function _normPath(p) {
    var parts = p.split('/');
    var r = [];
    for (var i = 0; i < parts.length; i++) {
        var seg = parts[i];
        if (seg === '..') { if (r.length) r.pop(); }
        else if (seg && seg !== '.') r.push(seg.toLowerCase());
    }
    return r.join('/');
}

function _joinPath(a, b) {
    return _normPath(a + '/' + b);
}

// Called from C# FindAllReferencesAsync.  symbolNames is a JS array (passed as a
// JSON array literal in the eval string).  programVersion is an int bumped by C#
// whenever a file is invalidated so the cache is rebuilt only when the program changes.
function findAllReferences(symbolNames, programVersion) {
    if (_refIndex === null || _refIndexVersion !== programVersion) {
        _refIndex = buildReferenceIndex();
        _refIndexVersion = programVersion;
    }
    var out = {};
    for (var i = 0; i < symbolNames.length; i++) {
        var refs = _refIndex[symbolNames[i]];
        if (refs && refs.length) out[symbolNames[i]] = refs;
    }
    return JSON.stringify(out);
}

// ── findReferences ────────────────────────────────────────────────────────────
// Single-symbol wrapper — delegates to findAllReferences so it also uses the
// inverted index instead of the broken text.indexOf anchor.
function findReferences(fileName, symbolName) {
    var json = findAllReferences([symbolName], _refIndexVersion);
    var allRefs = JSON.parse(json);
    return allRefs[symbolName] || [];
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
