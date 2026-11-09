#!/bin/bash

################################################## execute ##################################################
clear
git add -A
git commit -m "$(date '+%Y-%m-%d %H:%M:%S')"
sleep 2
git push
