#include "FlashRuntime.h"

#if defined(Q_OS_WIN)
#include <QAxObject>
#endif

bool hasNativeFlashSupport()
{
#if defined(Q_OS_WIN)
    QAxObject flash(QStringLiteral("ShockwaveFlash.ShockwaveFlash"));
    return !flash.isNull();
#else
    return false;
#endif
}
