#pragma once

#include "BrowserConfig.h"

class IBrowserBackend;
class QWidget;
class QObject;

IBrowserBackend *createBrowserBackend(const BrowserConfig &config, QWidget *hostWidget, QObject *parent);
