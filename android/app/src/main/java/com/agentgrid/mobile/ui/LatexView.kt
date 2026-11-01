package com.agentgrid.mobile.ui

import android.annotation.SuppressLint
import android.graphics.Color as AndroidColor
import android.util.Base64
import android.view.View
import android.webkit.JavascriptInterface
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import kotlinx.coroutines.delay

/**
 * LaTeX / Math 数学公式渲染视图（KaTeX 离线驱动）：
 * - 对标 CodeBlockView：高度随内容自然撑开不封顶，超宽支持块内横滚无滚动条，无全屏预览
 * - 语法正确 → WebView 纯净渲染，短公式居中，超宽公式从左边界起步不截断开头
 * - 语法异常 → throwOnError 触发降级，顶部标红并回退为纯净的 CodeBlockView 源码展示
 */
@Composable
fun LatexBlockView(
    source: String,
    modifier: Modifier = Modifier,
) {
    var isFailed by remember(source) { mutableStateOf(false) }
    var errorMessage by remember(source) { mutableStateOf<String?>(null) }
    var contentHeightPx by remember(source) { mutableFloatStateOf(0f) }

    val base64Code = remember(source) {
        Base64.encodeToString(source.toByteArray(Charsets.UTF_8), Base64.NO_WRAP)
    }

    if (isFailed) {
        Column(modifier = modifier.fillMaxWidth()) {
            if (!errorMessage.isNullOrBlank()) {
                Text(
                    text = "LATEX · 语法异常，降级显示源码: $errorMessage",
                    color = AgentGridColors.Red,
                    fontSize = 8.sp,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.padding(bottom = 2.dp),
                    maxLines = 1,
                )
            }
            CodeBlockView(MarkdownBlock.CodeBlock("latex", source))
        }
    } else {
        val inlineHtml = remember(base64Code) {
            buildLatexHtml(base64Code)
        }

        LaunchedEffect(source) {
            delay(15_000)
            if (contentHeightPx <= 0f && !isFailed) {
                isFailed = true
                errorMessage = "渲染超时"
            }
        }

        // 高度不封顶，由外层列表平滑滚动；仅设 48.dp 最小下限，绝不硬编码 600dp 上限截断
        val dynamicHeight = if (contentHeightPx > 0f) {
            (contentHeightPx.dp + 10.dp).coerceAtLeast(48.dp)
        } else {
            72.dp
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
                    text = "LATEX",
                    color = AgentGridColors.Cyan,
                    fontSize = 8.sp,
                    fontWeight = FontWeight.Bold,
                )
                MarkdownCopyButton(source = "$$\n$source\n$$")
            }

            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(dynamicHeight),
            ) {
                key(base64Code) {
                    AndroidView(
                        modifier = Modifier.fillMaxSize(),
                        factory = { context ->
                            try {
                                WebView(context).apply {
                                    configureInline()
                                    addJavascriptInterface(
                                        object {
                                            @JavascriptInterface
                                            fun onSuccess(height: Float) {
                                                post {
                                                    if (height > 0) contentHeightPx = height
                                                }
                                            }

                                            @JavascriptInterface
                                            fun onFailed(err: String) {
                                                post {
                                                    isFailed = true
                                                    errorMessage = err
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
                                            if (!request.isForMainFrame) return
                                            view.post {
                                                isFailed = true
                                                errorMessage = "页面加载失败（${error.errorCode}）"
                                            }
                                        }
                                    }
                                    loadDataWithBaseURL(
                                        "file:///android_asset/katex/",
                                        inlineHtml,
                                        "text/html",
                                        "UTF-8",
                                        null,
                                    )
                                }
                            } catch (e: Throwable) {
                                isFailed = true
                                errorMessage = e.message ?: "WebView 创建失败"
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

private fun buildLatexHtml(base64Code: String): String {
    return """
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes">
          <link rel="stylesheet" href="katex.min.css">
          <script src="katex.min.js"></script>
          <script src="contrib/mhchem.min.js"></script>
          <style>
            * {
              box-sizing: border-box;
              -webkit-tap-highlight-color: transparent;
            }
            html, body {
              margin: 0;
              padding: 0;
              background-color: transparent;
              color: #E8EDF5;
              font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, monospace;
            }
            #container {
              width: 100%;
              box-sizing: border-box;
              overflow-x: auto;
              overflow-y: hidden;
              -webkit-overflow-scrolling: touch;
              padding: 6px 4px;
              scrollbar-width: none;
              -ms-overflow-style: none;
            }
            #container::-webkit-scrollbar {
              display: none;
              width: 0;
              height: 0;
            }
            .katex-display {
              margin: 0 auto !important;
              padding: 0 !important;
              width: max-content;
              text-align: center;
            }
            .katex {
              font-size: 1.18em;
              color: #E8EDF5;
            }
          </style>
        </head>
        <body>
          <div id="container"></div>
          <script>
            try {
              const b64 = "$base64Code";
              const rawCode = new TextDecoder("utf-8").decode(
                Uint8Array.from(atob(b64), function(c) { return c.charCodeAt(0); })
              );

              const container = document.getElementById("container");
              katex.render(rawCode, container, {
                displayMode: true,
                throwOnError: true,
                trust: false,
                strict: false
              });

              if (window.AgentPagerBridge) {
                let lastReportedHeight = -1;
                const reportHeight = function() {
                  const container = document.getElementById("container");
                  let height = 0;
                  if (container) {
                    const rect = container.getBoundingClientRect();
                    if (rect.height > 0) {
                      height = Math.ceil(rect.height);
                    } else {
                      height = Math.ceil(container.scrollHeight);
                    }
                  }
                  if (height <= 0) {
                    height = Math.max(document.body.scrollHeight, document.documentElement.scrollHeight);
                  }
                  if (Math.abs(height - lastReportedHeight) < 1) return;
                  lastReportedHeight = height;
                  window.AgentPagerBridge.onSuccess(height);
                };

                reportHeight();
                if (window.ResizeObserver) {
                  const container = document.getElementById("container");
                  if (container) {
                    new ResizeObserver(reportHeight).observe(container);
                  }
                }
                setTimeout(reportHeight, 60);
                setTimeout(reportHeight, 200);
              }
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
    settings.apply {
        javaScriptEnabled = true
        domStorageEnabled = false
        allowFileAccess = true
        loadWithOverviewMode = true
        useWideViewPort = true
        displayZoomControls = false
        builtInZoomControls = false
        setSupportZoom(false)
    }
    setOnTouchListener { _, _ -> false }
}
