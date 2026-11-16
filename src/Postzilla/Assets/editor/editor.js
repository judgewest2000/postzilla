// Bridge between a single Monaco editor and the WinUI CodeEditor control (WebView2 web messages).
(function () {
    const host = window.chrome.webview;
    const post = (msg) => host.postMessage(msg);
    const pending = new Map();
    let nextId = 1;
    let editor = null;
    let applyingHostText = false;
    let cursorTimer = 0;

    const request = (msg) => new Promise((resolve) => {
        const id = nextId++;
        pending.set(id, resolve);
        post({ ...msg, id });
    });

    // Absolute URL so the worker (which resolves paths from its own location) finds the same files.
    require.config({ paths: { vs: new URL('../monaco/vs', location.href).href.replace(/\/$/, '') } });
    require(['vs/editor/editor.main'], () => {
        const transparent = { 'editor.background': '#00000000', 'editorGutter.background': '#00000000', 'minimap.background': '#00000000' };
        monaco.editor.defineTheme('bz-dark', { base: 'vs-dark', inherit: true, rules: [], colors: transparent });
        monaco.editor.defineTheme('bz-light', { base: 'vs', inherit: true, rules: [], colors: transparent });

        registerGraphQLCompletion();

        editor = monaco.editor.create(document.getElementById('container'), {
            value: '',
            language: 'plaintext',
            theme: 'bz-dark',
            automaticLayout: true,
            minimap: { enabled: false },
            fontFamily: "'Cascadia Mono', Consolas, monospace",
            fontSize: 13,
            lineHeight: 19,
            tabSize: 2,
            insertSpaces: true,
            scrollBeyondLastLine: false,
            folding: true,
            showFoldingControls: 'mouseover',
            lineNumbersMinChars: 3,
            renderLineHighlight: 'line',
            wordBasedSuggestions: 'off',
            fixedOverflowWidgets: true,
            stickyScroll: { enabled: false },
            padding: { top: 6, bottom: 6 },
            suggest: { showWords: false, preview: true },
            scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10, alwaysConsumeMouseWheel: false },
        });
        editor.getModel().setEOL(monaco.editor.EndOfLineSequence.LF);

        const K = monaco.KeyCode, M = monaco.KeyMod;
        editor.addCommand(M.CtrlCmd | K.Enter, () => post({ type: 'command', command: 'send' }));
        editor.addCommand(K.F5, () => post({ type: 'command', command: 'send' }));
        editor.addCommand(M.CtrlCmd | K.KeyS, () => post({ type: 'command', command: 'save' }));
        editor.addCommand(M.Shift | M.Alt | K.KeyF, () => post({ type: 'command', command: 'format' }));
        editor.addCommand(K.F1, () => post({ type: 'command', command: 'docs', offset: caretOffset() }));

        editor.onDidChangeModelContent(() => {
            updatePlaceholder();
            if (!applyingHostText) post({ type: 'change', text: editor.getValue() });
        });
        editor.onDidChangeCursorSelection((e) => {
            if (!e.selection.isEmpty()) return;
            clearTimeout(cursorTimer);
            cursorTimer = setTimeout(() => post({ type: 'cursor', offset: caretOffset() }), 120);
        });
        editor.onDidLayoutChange(updatePlaceholder);

        host.addEventListener('message', (e) => handle(e.data));
        post({ type: 'ready' });
    }, () => post({ type: 'loadError' }));

    function setTheme(theme) {
        monaco.editor.setTheme(theme === 'light' ? 'bz-light' : 'bz-dark');
        document.body.classList.toggle('light', theme === 'light');
    }

    function caretOffset() {
        return editor.getModel().getOffsetAt(editor.getPosition());
    }

    function updatePlaceholder() {
        const el = document.getElementById('placeholder');
        const empty = editor.getModel().getValueLength() === 0 && el.textContent.length > 0;
        el.style.display = empty ? 'block' : 'none';
        if (empty) {
            const layout = editor.getLayoutInfo();
            el.style.left = layout.contentLeft + 'px';
            el.style.top = '6px';
        }
    }

    function setText(text, undoable) {
        const model = editor.getModel();
        text = (text || '').replace(/\r\n?/g, '\n');
        if (text === model.getValue()) return;
        if (undoable && !editor.getOption(monaco.editor.EditorOption.readOnly)) {
            const position = editor.getPosition();
            editor.pushUndoStop();
            editor.executeEdits('host', [{ range: model.getFullModelRange(), text }]);
            editor.pushUndoStop();
            if (position) editor.setPosition(model.validatePosition(position));
            return;
        }
        applyingHostText = true;
        try {
            model.setValue(text);
        } finally {
            applyingHostText = false;
        }
    }

    function handle(msg) {
        switch (msg.type) {
            case 'init':
                document.getElementById('placeholder').textContent = msg.placeholder || '';
                setTheme(msg.theme);
                monaco.editor.setModelLanguage(editor.getModel(), msg.language || 'plaintext');
                editor.updateOptions({ readOnly: !!msg.readOnly, domReadOnly: !!msg.readOnly, wordWrap: msg.wordWrap ? 'on' : 'off', renderLineHighlight: msg.readOnly ? 'none' : 'line' });
                setText(msg.text, false);
                updatePlaceholder();
                break;
            case 'setText':
                setText(msg.text, !!msg.undoable);
                break;
            case 'setLanguage':
                monaco.editor.setModelLanguage(editor.getModel(), msg.language || 'plaintext');
                break;
            case 'setTheme':
                setTheme(msg.theme);
                break;
            case 'setPlaceholder':
                document.getElementById('placeholder').textContent = msg.placeholder || '';
                updatePlaceholder();
                break;
            case 'focus':
                editor.focus();
                break;
            case 'reveal':
                editor.setScrollTop(0);
                break;
            case 'response': {
                const resolve = pending.get(msg.id);
                pending.delete(msg.id);
                if (resolve) resolve(msg);
                break;
            }
        }
    }

    // Suggestions come from the C# GraphQLCompletionProvider, ranked server-side.
    function registerGraphQLCompletion() {
        const Kind = monaco.languages.CompletionItemKind;
        const kinds = {
            Keyword: Kind.Keyword, Field: Kind.Field, Argument: Kind.Property, InputField: Kind.Property,
            Type: Kind.Class, EnumValue: Kind.EnumMember, Variable: Kind.Variable, Fragment: Kind.Reference, Directive: Kind.Function,
        };
        monaco.languages.registerCompletionItemProvider('graphql', {
            triggerCharacters: ['$', '@', '(', ':', '{'],
            provideCompletionItems: async (model, position, context) => {
                const offset = model.getOffsetAt(position);
                const explicit = context.triggerKind === monaco.languages.CompletionTriggerKind.Invoke;
                const r = await request({ type: 'complete', text: model.getValue(), offset, explicit });
                if (!r.items || r.items.length === 0) return { suggestions: [] };

                let start = model.getPositionAt(Math.min(r.start, offset));
                if (start.lineNumber !== position.lineNumber) start = new monaco.Position(position.lineNumber, 1);
                const range = new monaco.Range(start.lineNumber, start.column, position.lineNumber, position.column);
                const typed = model.getValueInRange(range);

                return {
                    incomplete: true,
                    suggestions: r.items.map((it, i) => ({
                        label: { label: it.label, description: it.detail || undefined },
                        kind: kinds[it.kind] ?? Kind.Text,
                        insertText: it.insertText,
                        detail: it.detail || undefined,
                        documentation: it.description ? { value: (it.deprecated ? '**Deprecated.** ' : '') + it.description } : undefined,
                        tags: it.deprecated ? [monaco.languages.CompletionItemTag.Deprecated] : [],
                        sortText: String(i).padStart(5, '0'),
                        // Keep the server's ranking/filtering (prefix, substring, subsequence) instead of Monaco's fuzzy filter.
                        filterText: typed + it.label,
                        range,
                        command: it.insertText.endsWith(': ') ? { id: 'editor.action.triggerSuggest', title: 'Suggest values' } : undefined,
                    })),
                };
            },
        });
    }
})();
