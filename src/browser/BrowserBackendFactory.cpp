#include "BrowserBackendFactory.h"

#include "FlashRuntime.h"
#include "IBrowserBackend.h"
#include "WebEngineBackend.h"
#include "../network/ProxyManager.h"

#if defined(Q_OS_WIN)
#include "WindowsIeBackend.h"
#endif

IBrowserBackend *createBrowserBackend(const BrowserConfig &config, QWidget *hostWidget, QObject *parent)
{
#if defined(Q_OS_WIN)
    if (hasNativeFlashSupport() && !ProxyManager::instance().shouldForceChromiumBackend()) {
        return new WindowsIeBackend(config, hostWidget, parent);
    }
#else
#endif
    return new WebEngineBackend(config, hostWidget, parent);
}
