#include "BrowserBackendFactory.h"

#include "IBrowserBackend.h"

#if defined(Q_OS_WIN)
#include "WindowsIeBackend.h"
#else
#include "WebEngineBackend.h"
#endif

IBrowserBackend *createBrowserBackend(const BrowserConfig &config, QWidget *hostWidget, QObject *parent)
{
#if defined(Q_OS_WIN)
    return new WindowsIeBackend(config, hostWidget, parent);
#else
    return new WebEngineBackend(config, hostWidget, parent);
#endif
}
