package com.agentgrid.mobile.ui

import android.annotation.SuppressLint
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color as AndroidColor
import android.util.Base64
import android.util.LruCache
import android.view.View
import android.webkit.JavascriptInterface
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.agentgrid.mobile.domain.MermaidSecurityLevel
import kotlinx.coroutines.delay

val LocalMermaidSecurityLevel = compositionLocalOf { MermaidSecurityLevel.DEFAULT }

/**
 * ```mermaid 代码块的渲染视图：
 * - 语法正确 → WebView 内联静态渲染（自适应高度，不抢手势），点击进入全屏查看
 * - 语法异常 → 立即回退为源码展示（标准的 CodeBlockView），无死锁卡死
 * - 全屏查看 → 独立全屏画布，支持双指缩放与自由平移，按系统返回键或关闭按钮退出
 */
@Composable
fun MermaidBlockView(
    source: String,
    modifier: Modifier = Modifier,
    securityLevel: String = LocalMermaidSecurityLevel.current,
) {
    val density = LocalDensity.current
    var isFailed by remember(source, securityLevel) { mutableStateOf(false) }
    var errorMessage by remember(source, securityLevel) { mutableStateOf<String?>(null) }
    var contentHeightPx by remember(source, securityLevel) { mutableFloatStateOf(0f) }
    var fullscreen by remember(source) { mutableStateOf(false) }

    val base64Code = remember(source) {
        Base64.encodeToString(source.toByteArray(Charsets.UTF_8), Base64.NO_WRAP)
    }

    if (isFailed) {
        // 语法异常：直接以源码展示，顶部带红色小字提示
        Column(modifier = modifier.fillMaxWidth()) {
            if (!errorMessage.isNullOrBlank()) {
                Text(
                    text = "MERMAID · 语法异常，降级显示源码: $errorMessage",
                    color = AgentGridColors.Red,
                    fontSize = 8.sp,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.padding(bottom = 2.dp),
                    maxLines = 1,
                )
            }
            CodeBlockView(MarkdownBlock.CodeBlock("mermaid", source))
        }
    } else {
        // 语法正确：内嵌自适应渲染，点击任意区域进入全屏查看
        val inlineHtml = remember(base64Code, securityLevel) {
            buildMermaidHtml(base64Code, isInteractive = false, securityLevel = securityLevel)
        }
        // 记录已加载的 html（按引用比较）：只有源码真的变了才重载，避免每次重组都把 3.5MB 脚本重跑一遍
        val loadedHtml = remember { arrayOfNulls<String>(1) }
        val webViewRef = remember(source, securityLevel) { arrayOfNulls<WebView>(1) }
        // 注意：WebView JS 传出的尺寸是 CSS 像素（因配置了 width=device-width，1 CSS px 在 Android 上已等价于 1 dp）。
        // 禁止在此调用 .toDp()，否则会再次除以屏幕密度（density）导致画框高度缩水近 3 倍并截断下半部分。
        val dynamicHeight = if (contentHeightPx > 0f) {
            (contentHeightPx.dp + 12.dp).coerceIn(80.dp, 600.dp)
        } else {
            160.dp
        }

        Column(
            modifier = modifier
                .fillMaxWidth()
                .border(1.dp, AgentGridColors.Divider)
                .background(AgentGridColors.Background)
                .padding(8.dp),
        ) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 4.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = "MERMAID",
                    color = AgentGridColors.Cyan,
                    fontSize = 8.sp,
                    fontWeight = FontWeight.Bold,
                )
                Row(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    MarkdownCopyButton(source = "```mermaid\n$source\n```")
                    Text(
                        text = "[ 全屏 ]",
                        color = AgentGridColors.Cyan,
                        fontSize = 9.sp,
                        fontWeight = FontWeight.Bold,
                        modifier = Modifier.clickable { fullscreen = true },
                    )
                }
            }

            BoxWithConstraints(
                modifier = Modifier.fillMaxWidth(),
            ) {
                val boxWidthPx = constraints.maxWidth
                // 渲染过的宽度直接复用截图：滚出屏幕再回来不再重建 WebView、重跑 3.5MB 脚本
                val cached = remember(source, boxWidthPx, securityLevel) {
                    MermaidBitmapCache.get(MermaidBitmapCache.key(source, boxWidthPx, securityLevel))
                }
                val boxHeight = if (contentHeightPx > 0f) {
                    dynamicHeight
                } else {
                    // 缓存的 Bitmap 尺寸是 Android 物理像素（px），转 Dp 必须除以 density
                    cached
                        ?.let { with(density) { it.height.toDp() } }
                        ?.coerceIn(80.dp, 600.dp)
                        ?: dynamicHeight
                }

                // 20 秒兜底：既没有缓存、也没有任何回调时才降级源码。
                // 慢设备上多张图同时渲染时，10 秒会误杀还在排队的图。
                LaunchedEffect(source, cached != null, securityLevel) {
                    if (cached != null) return@LaunchedEffect
                    delay(20_000)
                    if (contentHeightPx <= 0f && !isFailed) {
                        errorMessage = "渲染超时"
                        isFailed = true
                    }
                }

                if (cached == null) {
                    // 首次渲染（或新的宽度）：等高度稳定后抓一张图存缓存；
                    // 当前这块仍用 WebView，不切换，避免抓到空图时把可见内容换掉。
                    LaunchedEffect(source, boxWidthPx, contentHeightPx, securityLevel) {
                        if (contentHeightPx <= 0f) return@LaunchedEffect
                        delay(250)
                        val view = webViewRef[0] ?: return@LaunchedEffect
                        if (view.width <= 0 || view.height <= 0 || view.width != boxWidthPx) {
                            return@LaunchedEffect
                        }
                        withFrameNanos { }
                        captureWebView(view)?.let { bitmap ->
                            MermaidBitmapCache.put(
                                MermaidBitmapCache.key(source, view.width, securityLevel),
                                bitmap,
                            )
                        }
                    }
                }

                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(boxHeight),
                    contentAlignment = Alignment.Center,
                ) {
                    if (cached != null) {
                        Image(
                            bitmap = cached.asImageBitmap(),
                            contentDescription = null,
                            modifier = Modifier.fillMaxSize(),
                            contentScale = ContentScale.Fit,
                        )
                    } else {
                        key(source, securityLevel) {
                        AndroidView(
                            modifier = Modifier.fillMaxSize(),
                            factory = { context ->
                                try {
                                    WebView(context).apply {
                                        configureInline()
                                        webViewRef[0] = this
                                        addJavascriptInterface(
                                            object {
                                                @JavascriptInterface
                                                fun onSuccess(heightPx: Float) {
                                                    post {
                                                        contentHeightPx = heightPx
                                                        isFailed = false
                                                    }
                                                }

                                                @JavascriptInterface
                                                fun onFailed(error: String) {
                                                    post {
                                                        errorMessage = error
                                                        isFailed = true
                                                    }
                                                }
                                            },
                                            "AgentPagerBridge",
                                        )
                                        webViewClient = object : WebViewClient() {
                                            override fun onReceivedError(
                                                view: WebView,
                                                request: WebResourceRequest,
                                                error: WebResourceError,
                                            ) {
                                                // 只认主文档失败；子资源噪音（favicon 之类）不判死
                                                if (!request.isForMainFrame) return
                                                post {
                                                    errorMessage = "页面加载失败（错误码 ${error.errorCode}）"
                                                    isFailed = true
                                                }
                                            }
                                        }
                                        loadDataWithBaseURL(
                                            "file:///android_asset/mermaid/",
                                            inlineHtml,
                                            "text/html",
                                            "UTF-8",
                                            null,
                                        )
                                        loadedHtml[0] = inlineHtml
                                    }
                                } catch (e: Throwable) {
                                    errorMessage = e.message
                                    isFailed = true
                                    View(context)
                                }
                            },
                            update = { view ->
                                val webView = view as? WebView
                                if (webView != null) {
                                    webViewRef[0] = webView
                                    // 只在 html 真的换了才重载（之前每次重组都 load 一遍，又卡又闪）
                                    if (loadedHtml[0] !== inlineHtml) {
                                        loadedHtml[0] = inlineHtml
                                        webView.loadDataWithBaseURL(
                                            "file:///android_asset/mermaid/",
                                            inlineHtml,
                                            "text/html",
                                            "UTF-8",
                                            null,
                                        )
                                    }
                                }
                            },
                            onRelease = { view ->
                                webViewRef[0] = null
                                (view as? WebView)?.destroy()
                            },
                        )
                        }
                    }

                    // 核心修复：覆盖在 WebView/截图上层，保证 100% 捕获点击唤起全屏
                    Box(
                        modifier = Modifier
                            .matchParentSize()
                            .clickable { fullscreen = true },
                    )
                }
            }
        }
    }

    // 全屏查看：点击渲染卡片展开，支持双指缩放拖动，返回键退出
    if (fullscreen) {
        MermaidFullscreenDialog(
            base64Code = base64Code,
            securityLevel = securityLevel,
            onDismiss = { fullscreen = false },
        )
    }
}

@Composable
private fun MermaidFullscreenDialog(
    base64Code: String,
    securityLevel: String,
    onDismiss: () -> Unit,
) {
    val fullscreenHtml = remember(base64Code, securityLevel) {
        buildMermaidHtml(base64Code, isInteractive = true, securityLevel = securityLevel)
    }

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(
            usePlatformDefaultWidth = false,
            decorFitsSystemWindows = false,
        ),
    ) {
        // 关键：拦截系统返回键，返回键退出全屏
        BackHandler(onBack = onDismiss)
        // 横屏专用模式下 Activity 已隐藏系统栏；对话框是独立窗口，需同样隐藏，
        // 否则 safeDrawing 会把状态栏高度算成内边距，顶部白白浪费一条。
        MatchActivitySystemBars()

        Column(
            modifier = Modifier
                .fillMaxSize()
                .background(AgentGridColors.Background)
                .windowInsetsPadding(WindowInsets.safeDrawing),
        ) {
            // 顶部工具栏
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(AgentGridColors.SurfaceRaised)
                    .padding(horizontal = 14.dp, vertical = 10.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(
                        text = "MERMAID · 全屏",
                        color = AgentGridColors.Cyan,
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Bold,
                    )
                }

                Text(
                    text = "[ 关闭 ]",
                    color = AgentGridColors.Amber,
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.clickable { onDismiss() },
                )
            }

            // 全屏交互式画布：完整手势缩放和平移
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
            ) {
                key(base64Code, securityLevel) {
                    AndroidView(
                        modifier = Modifier.fillMaxSize(),
                        factory = { context ->
                            try {
                                WebView(context).apply {
                                    configureInteractive()
                                    webViewClient = object : WebViewClient() {}
                                    loadDataWithBaseURL(
                                        "file:///android_asset/mermaid/",
                                        fullscreenHtml,
                                        "text/html",
                                        "UTF-8",
                                        null,
                                    )
                                }
                            } catch (_: Throwable) {
                                View(context)
                            }
                        },
                        onRelease = { view ->
                            (view as? WebView)?.destroy()
                        },
                    )
                }
            }
        }
    }
}

private fun buildMermaidHtml(
    base64Code: String,
    isInteractive: Boolean,
    securityLevel: String = MermaidSecurityLevel.DEFAULT,
): String {
    val safeSecurityLevel = MermaidSecurityLevel.normalize(securityLevel)
    val overflowCss = if (isInteractive) "auto" else "hidden"
    val touchActionCss = if (isInteractive) "pan-x pan-y pinch-zoom" else "none"
    val pointerEventsCss = if (isInteractive) "auto" else "none"
    val paddingPxInt = if (isInteractive) 20 else 4
    val paddingPx = "${paddingPxInt}px"
    val alignItemsCss = if (isInteractive) "center" else "flex-start"
    val minHeightCss = if (isInteractive) "min-height: 100%;" else ""

    return """
        <!DOCTYPE html>
        <html>
        <head>
        <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes">
        <style>
          * { box-sizing: border-box; }
          html, body {
            margin: 0;
            padding: 0;
            width: 100%;
            $minHeightCss
            background-color: #05070B;
            color: #E8EDF5;
            overflow: $overflowCss;
            touch-action: $touchActionCss;
            pointer-events: $pointerEventsCss;
            font-family: monospace;
            -webkit-user-select: none;
            user-select: none;
          }
          #wrapper {
            display: flex;
            justify-content: center;
            align-items: $alignItemsCss;
            min-width: 100%;
            $minHeightCss
            padding: $paddingPx;
          }
          #container {
            display: flex;
            justify-content: center;
            align-items: $alignItemsCss;
            width: 100%;
          }
          svg {
            max-width: 100%;
            height: auto;
          }
        </style>
        <script src="file:///android_asset/mermaid/mermaid.min.js"></script>
        </head>
        <body>
        <div id="wrapper"><div id="container"></div></div>
        <script>
          try {
            mermaid.initialize({
              startOnLoad: false,
              theme: 'dark',
              securityLevel: '$safeSecurityLevel',
              suppressErrorRendering: true,
              darkMode: true,
              fontFamily: 'monospace',
              themeVariables: {
                darkMode: true,
                background: '#05070B',
                mainBkg: '#101824',
                nodeBorder: '#5EEAD4',
                nodeTextColor: '#E8EDF5',
                primaryColor: '#101824',
                primaryBorderColor: '#5EEAD4',
                primaryTextColor: '#E8EDF5',
                lineColor: '#7F8A9A',
                edgeLabelBackground: '#05070B',
                secondaryColor: '#18212E',
                tertiaryColor: '#0B1018',
                clusterBkg: '#0B1018',
                clusterBorder: '#18212E'
              }
            });

            const b64 = "$base64Code";
            const rawCode = new TextDecoder("utf-8").decode(
              Uint8Array.from(atob(b64), function(c) { return c.charCodeAt(0); })
            );

            // 中文/括号容错：mermaid lexer 的未加引号文本只接受
            // [\u0020-\u007E] + [\u3131-\u318E\u31A0-\u31BA\u31F0-\u31FF\u3400-\u4DB5\u4E00-\u9FCC]
            // （实测：全角标点、半角括号/方括号嵌进标签、嵌套括号、子图标题带括号都会 Parse error）
            // 命中即整段包双引号——grammar 的 text/edgeText/textNoTags 都接受整体引号串，显示文本不变。
            const SAFE_CHAR = /^[\u0020-\u007E\u3131-\u318E\u31A0-\u31BA\u31F0-\u31FF\u3400-\u4DB5\u4E00-\u9FCC]$/;
            const STRUCT_CHAR = /[()\[\]{}"]/;
            function needsQuote(text) {
              const trimmed = text.trim();
              if (!trimmed) return false;
              if (trimmed.startsWith('"') && trimmed.endsWith('"')) return false;
              for (let k = 0; k < trimmed.length; k++) {
                const ch = trimmed[k];
                if (STRUCT_CHAR.test(ch)) return true;
                if (!SAFE_CHAR.test(ch)) return true;
              }
              return false;
            }
            const DELIMS = [
              { open: '[[', close: ']]' },
              { open: '((', close: '))' },
              { open: '{{', close: '}}' },
              { open: '([', close: '])' },
              { open: '[(', close: ')]' },
              { open: '[\\', close: '\\]' },
              { open: '[/', close: '/]' },
              { open: '[', close: ']' },
              { open: '{', close: '}' },
              { open: '(', close: ')' }
            ];

            function findClose(line, from, open, close) {
              let depth = 1;
              let inQuote = false;
              for (let j = from; j <= line.length - close.length; j++) {
                const ch = line[j];
                if (ch === '"') {
                  inQuote = !inQuote;
                  continue;
                }
                if (inQuote) continue;
                if (open !== close && line.startsWith(open, j)) {
                  depth++;
                  j += open.length - 1;
                  continue;
                }
                if (line.startsWith(close, j)) {
                  depth--;
                  if (depth === 0) return j;
                  j += close.length - 1;
                  continue;
                }
              }
              return -1;
            }

            function repairLine(line) {
              if (/^\s*%%/.test(line)) return line;

              // 1. 连线文字: |...|
              line = line.replace(/\|([^|\r\n]+)\|/g, function(match, inner) {
                const trimmed = inner.trim();
                if (trimmed.startsWith('"') && trimmed.endsWith('"')) return match;
                if (needsQuote(trimmed)) {
                  return '|"' + trimmed.replace(/"/g, '#quot;') + '"|';
                }
                return match;
              });

              // 2. subgraph 标题（只有 id[...] 形式交给形状修复；带半角括号的标题这里也要包）
              line = line.replace(/^(\s*subgraph\s+)(.+)$/, function(match, prefix, title) {
                const trimmed = title.trim();
                if (trimmed.startsWith('"') && trimmed.endsWith('"')) return match;
                if (/[\[\]]/.test(trimmed)) return match;
                if (needsQuote(trimmed)) {
                  return prefix + '"' + trimmed.replace(/"/g, '#quot;') + '"';
                }
                return match;
              });

              // 3. 形状节点扫描（自左向右线性扫描，杜绝嵌套误伤）
              let result = '';
              let i = 0;
              while (i < line.length) {
                let matched = false;
                for (let d = 0; d < DELIMS.length; d++) {
                  const open = DELIMS[d].open;
                  const close = DELIMS[d].close;
                  if (line.startsWith(open, i)) {
                    const closeIdx = findClose(line, i + open.length, open, close);
                    if (closeIdx !== -1) {
                      const inner = line.slice(i + open.length, closeIdx);
                      const trimmed = inner.trim();
                      if (!trimmed.startsWith('"') && needsQuote(trimmed)) {
                        const quoted = '"' + trimmed.replace(/"/g, '#quot;') + '"';
                        result += open + quoted + close;
                      } else {
                        result += open + inner + close;
                      }
                      i = closeIdx + close.length;
                      matched = true;
                      break;
                    }
                  }
                }
                if (!matched) {
                  result += line[i];
                  i++;
                }
              }
              return result;
            }

            function sanitizeMermaid(source) {
              return source.split('\n').map(repairLine).join('\n');
            }
            // 暴露给离线回归测试（本地页面，无外泄面）
            window.agentpagerSanitize = sanitizeMermaid;

            let renderSeq = 0;
            function doRender(code) {
              const renderId = "mermaid_svg_" + Date.now() + "_" + (++renderSeq);
              return mermaid.render(renderId, code);
            }

            // 优先按原代码渲染；失败且存在未加引号的危险字符时，做容错修复再重试一次
            doRender(rawCode).catch(function(err) {
              const sanitized = sanitizeMermaid(rawCode);
              if (sanitized !== rawCode) {
                return doRender(sanitized);
              }
              throw err;
            }).then(function(res) {
              document.getElementById("container").innerHTML = res.svg;
              if (window.AgentPagerBridge) {
                // 读取 SVG 渲染后的真实像素边界，监听 #container 变动保证回流后重新精确上报
                let lastReportedHeight = -1;
                const reportHeight = function(force) {
                  let height = 0;
                  const container = document.getElementById("container");
                  const svg = document.querySelector("#container svg");
                  if (svg) {
                    const rect = svg.getBoundingClientRect();
                    if (rect.height > 0) {
                      height = Math.ceil(rect.height + ($paddingPxInt * 2));
                    }
                  }
                  if (height <= 0 && container) {
                    height = Math.ceil(container.scrollHeight + ($paddingPxInt * 2));
                  }
                  if (height <= 0) {
                    height = Math.max(document.body.scrollHeight, document.documentElement.scrollHeight);
                  }
                  if (!force && Math.abs(height - lastReportedHeight) < 1) return;
                  lastReportedHeight = height;
                  window.AgentPagerBridge.onSuccess(height);
                };
                reportHeight(true);
                const scheduleReport = function() {
                  requestAnimationFrame(function() { reportHeight(true); });
                };
                if (window.ResizeObserver) {
                  const container = document.getElementById("container");
                  if (container) {
                    new ResizeObserver(scheduleReport).observe(container);
                  }
                }
                window.addEventListener("resize", scheduleReport);
                setTimeout(scheduleReport, 60);
                setTimeout(scheduleReport, 240);
              }
            }).catch(function(err) {
              if (window.AgentPagerBridge) {
                window.AgentPagerBridge.onFailed(err.message || String(err));
              }
            });
          } catch(e) {
            if (window.AgentPagerBridge) {
              window.AgentPagerBridge.onFailed(e.message || String(e));
            }
          }
        </script>
        </body>
        </html>
    """.trimIndent()
}

@SuppressLint("SetJavaScriptEnabled", "ClickableViewAccessibility")
private fun WebView.configureInline() {
    setBackgroundColor(AndroidColor.TRANSPARENT)
    isVerticalScrollBarEnabled = false
    isHorizontalScrollBarEnabled = false
    overScrollMode = View.OVER_SCROLL_NEVER
    setOnTouchListener { _, _ -> false }
    settings.apply {
        javaScriptEnabled = true
        domStorageEnabled = true
        allowFileAccess = true
        builtInZoomControls = false
        displayZoomControls = false
        setSupportZoom(false)
        useWideViewPort = true
        loadWithOverviewMode = true
    }
}

@SuppressLint("SetJavaScriptEnabled")
private fun WebView.configureInteractive() {
    setBackgroundColor(AndroidColor.TRANSPARENT)
    isVerticalScrollBarEnabled = true
    isHorizontalScrollBarEnabled = true
    overScrollMode = View.OVER_SCROLL_IF_CONTENT_SCROLLS
    settings.apply {
        javaScriptEnabled = true
        domStorageEnabled = true
        allowFileAccess = true
        builtInZoomControls = true
        displayZoomControls = false
        setSupportZoom(true)
        useWideViewPort = true
        loadWithOverviewMode = true
    }
}

fun clearMermaidCache() {
    MermaidBitmapCache.clear()
}

private object MermaidBitmapCache {
    private const val MAX_BYTES = 24 * 1024 * 1024

    private val cache = object : LruCache<String, Bitmap>(MAX_BYTES) {
        override fun sizeOf(key: String, value: Bitmap): Int = value.byteCount
    }

    fun key(source: String, widthPx: Int, securityLevel: String): String =
        "${source.hashCode()}:$widthPx:$securityLevel"

    fun get(key: String): Bitmap? = cache.get(key)

    fun put(key: String, value: Bitmap) {
        cache.put(key, value)
    }

    fun clear() {
        cache.evictAll()
    }
}

/**
 * 抓当前可见 WebView 的位图。WebView 的硬件层通常画不进软件 Canvas：
 * 先直接画；得到空图（只剩页面底色）就临时切软件层重试一次，再切回硬件层。
 * 抓不到或抓到空图返回 null，调用方不缓存，保持原 WebView 行为。
 */
private suspend fun captureWebView(view: WebView): Bitmap? {
    return try {
        var bitmap = drawWebView(view)
        if (!looksRendered(bitmap)) {
            view.setLayerType(View.LAYER_TYPE_SOFTWARE, null)
            withFrameNanos { }
            bitmap = drawWebView(view)
            view.setLayerType(View.LAYER_TYPE_HARDWARE, null)
        }
        if (looksRendered(bitmap)) bitmap else null
    } catch (_: Throwable) {
        null
    }
}

private fun drawWebView(view: WebView): Bitmap {
    val bitmap = Bitmap.createBitmap(view.width, view.height, Bitmap.Config.ARGB_8888)
    view.draw(Canvas(bitmap))
    return bitmap
}

/** 页面底色是 #05070B；采样一圈，全是底色就视为空图/抓取失败。 */
private fun looksRendered(bitmap: Bitmap): Boolean {
    val background = 0xFF05070B.toInt()
    val stepX = (bitmap.width / 32).coerceAtLeast(1)
    val stepY = (bitmap.height / 32).coerceAtLeast(1)
    var y = 0
    while (y < bitmap.height) {
        var x = 0
        while (x < bitmap.width) {
            if (bitmap.getPixel(x, y) != background) return true
            x += stepX
        }
        y += stepY
    }
    return false
}
