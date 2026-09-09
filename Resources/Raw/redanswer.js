function loadRedAnswers(answerType, htmlContent) {
    try {
        console.log('Loading ' + answerType + '...');
        
        var container = document.querySelector('.content-overlay');
        if (!container) {
            container = document.querySelector('.page-container');
        }
        if (!container) {
            container = document.body;
        }
        console.log('Container found:', container);
        
        var wrapperId = answerType + 'Wrapper';
        if (document.getElementById(wrapperId)) {
            console.log('Wrapper already exists:', wrapperId);
            return;
        }
        
        var wrapper = document.createElement('div');
        wrapper.id = wrapperId;
        wrapper.setAttribute('redanswertype', answerType);
        wrapper.style.display = 'none';
        wrapper.style.position = 'absolute';
        wrapper.style.top = '0';
        wrapper.style.left = '0';
        wrapper.style.width = '100%';
        wrapper.style.height = '100%';
        wrapper.style.pointerEvents = 'none';
        wrapper.style.zIndex = '50';
        
        wrapper.innerHTML = htmlContent;
        
        var elements = wrapper.querySelectorAll('[redanswertype]');
        console.log('Found ' + elements.length + ' elements with redanswertype');
        
        for (var i = 0; i < elements.length; i++) {
            var currentType = elements[i].getAttribute('redanswertype');
            if (currentType === 'studentAnswers') {
                elements[i].setAttribute('redanswertype', answerType);
            }
            elements[i].style.display = 'none';
            elements[i].classList.remove('visible');
        }
        
        container.appendChild(wrapper);
        console.log('Successfully injected ' + answerType + ' with ' + elements.length + ' elements');
        return 'success';
    } catch(e) {
        console.error('Error injecting ' + answerType + ':', e.message);
        return 'error';
    }
}

function toggleRedAnswers(type) {
    try {
        var elements = document.querySelectorAll('[redanswertype="' + type + '"]');
        console.log('Found ' + elements.length + ' elements with type ' + type);
        
        if (elements.length === 0) {
            console.log('No elements found with type ' + type);
            return;
        }
        
        var anyVisible = false;
        for (var i = 0; i < elements.length; i++) {
            var display = window.getComputedStyle(elements[i]).display;
            if (display !== 'none') {
                anyVisible = true;
                break;
            }
        }
        
        console.log('Any visible:', anyVisible);
        
        for (var i = 0; i < elements.length; i++) {
            if (anyVisible) {
                elements[i].style.display = 'none';
                elements[i].classList.remove('visible');
            } else {
                elements[i].style.display = 'inline-block';
                elements[i].classList.add('visible');
            }
        }
        
        console.log('Toggled ' + elements.length + ' elements');
        return elements.length;
    } catch(e) {
        console.error('Error toggling ' + type + ':', e.message);
        return 0;
    }
}

function checkRedAnswersExist(type) {
    try {
        var elements = document.querySelectorAll('[redanswertype="' + type + '"]');
        return elements.length > 0;
    } catch(e) {
        console.error('Error checking ' + type + ':', e.message);
        return false;
    }
}

function resetRedAnswers() {
    try {
        var wrappers = document.querySelectorAll('[id$="Wrapper"]');
        for (var i = 0; i < wrappers.length; i++) {
            wrappers[i].parentNode.removeChild(wrappers[i]);
        }
        console.log('Reset all answer wrappers');
    } catch(e) {
        console.error('Error resetting answers:', e.message);
    }
}
